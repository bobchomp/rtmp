using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RTMPProjector.Services;

public class CloudflaredService : IAsyncDisposable
{
    private const string GitHubApiUrl =
        "https://api.github.com/repos/cloudflare/cloudflared/releases/latest";

    private static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RTMPProjector");

    // cloudflared stores its own files under %USERPROFILE%\.cloudflared by default
    private static readonly string CfDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".cloudflared");

    public static string ExePath    => Path.Combine(AppDir, "cloudflared.exe");
    public static string ConfigPath => Path.Combine(AppDir, "tunnel.yml");
    private static string CertPath  => Path.Combine(CfDir, "cert.pem");

    private Process? _process;
    private readonly HttpClient _http;

    public bool IsRunning  => _process is { HasExited: false };
    public bool IsLoggedIn => File.Exists(CertPath);
    public bool HasBinary  => File.Exists(ExePath);

    public bool HasCredentials(string tunnelId) =>
        !string.IsNullOrEmpty(tunnelId) &&
        File.Exists(Path.Combine(CfDir, $"{tunnelId}.json"));

    public event Action<string>? LogMessage;

    public CloudflaredService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RTMPProjector", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    public async Task<bool> EnsureBinaryAsync(IProgress<string>? progress = null)
    {
        if (HasBinary) return true;
        Directory.CreateDirectory(AppDir);

        try
        {
            progress?.Report("Looking up latest cloudflared release…");
            var url = await ResolveDownloadUrlAsync();

            progress?.Report("Downloading cloudflared…");
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            await using var fs = File.Create(ExePath);
            await resp.Content.CopyToAsync(fs);

            progress?.Report("cloudflared downloaded.");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Download failed: {ex.Message}");
            return false;
        }
    }

    private async Task<string> ResolveDownloadUrlAsync()
    {
        var json = await _http.GetStringAsync(GitHubApiUrl);
        using var doc = JsonDocument.Parse(json);

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Equals("cloudflared-windows-amd64.exe", StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("Asset has no download URL.");
        }
        throw new InvalidOperationException("cloudflared Windows binary not found in release.");
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    /// Opens a browser for the user to authorize. Polls for cert.pem rather than
    /// waiting for process exit, since cloudflared doesn't always exit cleanly.
    public async Task<bool> LoginAsync(IProgress<string>? progress = null,
                                       CancellationToken ct = default)
    {
        progress?.Report("Opening browser — sign in to Cloudflare and click Authorize, then return here…");

        var psi = BuildPsi("tunnel login", redirectOutput: true);
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[cf-login] {e.Data}"); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[cf-login] {e.Data}"); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Poll for cert.pem — don't rely on process exit which varies by cloudflared version
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(5));

        while (!linked.Token.IsCancellationRequested)
        {
            if (IsLoggedIn)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                progress?.Report("Cloudflare authorization complete.");
                return true;
            }

            if (proc.HasExited)
                break;

            await Task.Delay(1000, linked.Token).ConfigureAwait(false);
        }

        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }

        if (IsLoggedIn)
        {
            progress?.Report("Cloudflare authorization complete.");
            return true;
        }

        progress?.Report(ct.IsCancellationRequested
            ? "Authorization cancelled."
            : "Authorization timed out or cert.pem was not created. Try again.");
        return false;
    }

    // ── Create tunnel ─────────────────────────────────────────────────────────

    public async Task<string?> CreateTunnelAsync(string tunnelName,
                                                  IProgress<string>? progress = null)
    {
        progress?.Report($"Creating tunnel \"{tunnelName}\"…");

        var (stdout, _) = await RunAsync($"tunnel create \"{tunnelName}\"");

        // Output: "Created tunnel NAME with id <uuid>"
        var m = Regex.Match(stdout, @"with id ([0-9a-f\-]{36})", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            progress?.Report($"Tunnel ready.");
            return m.Groups[1].Value;
        }

        // Tunnel may already exist — try to list and find it
        progress?.Report($"Checking for existing tunnel \"{tunnelName}\"…");
        var (listOut, _) = await RunAsync("tunnel list --output json");
        try
        {
            using var doc = JsonDocument.Parse(listOut.Trim().Length > 0 ? listOut : "[]");
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.TryGetProperty("name", out var nameEl) &&
                    nameEl.GetString()?.Equals(tunnelName, StringComparison.OrdinalIgnoreCase) == true &&
                    t.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    progress?.Report("Using existing tunnel.");
                    return id;
                }
            }
        }
        catch { }

        progress?.Report("Could not create or find tunnel.");
        return null;
    }

    // ── Route DNS ─────────────────────────────────────────────────────────────

    public async Task<bool> RouteDnsAsync(string tunnelName, string hostname,
                                          IProgress<string>? progress = null)
    {
        progress?.Report($"Adding DNS record for {hostname}…");

        var (_, exitCode) = await RunAsync($"tunnel route dns \"{tunnelName}\" \"{hostname}\"");

        var ok = exitCode == 0;
        progress?.Report(ok ? "DNS record configured." : "DNS setup failed — check your Cloudflare zone.");
        return ok;
    }

    // ── Write config ──────────────────────────────────────────────────────────

    public void WriteConfig(string tunnelId, string streamHostname, int hlsPort,
                             string playerHostname, int playerPort)
    {
        var credPath = Path.Combine(CfDir, $"{tunnelId}.json").Replace('\\', '/');

        var playerIngress = string.IsNullOrWhiteSpace(playerHostname) ? "" : $"""

              - hostname: {playerHostname}
                service: http://localhost:{playerPort}
            """;

        var config = $"""
            tunnel: {tunnelId}
            credentials-file: {credPath}

            ingress:
              - hostname: {streamHostname}
                service: http://localhost:{hlsPort}{playerIngress}
              - service: http_status:404
            """;

        Directory.CreateDirectory(AppDir);
        File.WriteAllText(ConfigPath, config);
    }

    // ── Start / stop ──────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (IsRunning) return;

        if (!HasBinary || !File.Exists(ConfigPath))
            throw new InvalidOperationException("cloudflared is not configured. Run web stream setup first.");

        var psi = BuildPsi($"tunnel --config \"{ConfigPath}\" run", redirectOutput: true);
        psi.WorkingDirectory = AppDir;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[tunnel] {e.Data}"); };
        _process.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[tunnel] {e.Data}"); };
        _process.Exited += (_, _) => LogMessage?.Invoke("[tunnel] cloudflared exited.");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(1500);
    }

    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        _process = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProcessStartInfo BuildPsi(string args, bool redirectOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = ExePath,
            Arguments = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError  = redirectOutput,
        };
        return psi;
    }

    private async Task<(string output, int exitCode)> RunAsync(string args)
    {
        var psi = BuildPsi(args, redirectOutput: true);
        var sb  = new System.Text.StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        LogMessage?.Invoke(sb.ToString().Trim());
        return (sb.ToString(), proc.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
