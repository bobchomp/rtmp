using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RTMPProjector.Services;

/// <summary>Checksum + safe-extract helpers for the binaries the app downloads itself.</summary>
public static class DownloadVerifier
{
    /// Throws if the file's SHA-256 doesn't match <paramref name="expectedHex"/>.
    public static async Task VerifySha256Async(string path, string expectedHex)
    {
        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        if (!actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)} failed its SHA-256 checksum check.");
    }

    /// <summary>
    /// Extracts the single zip entry whose name ends with <paramref name="entrySuffix"/> to
    /// <paramref name="destination"/>, via a temp file so a crash can never leave a
    /// truncated exe that later passes a File.Exists check.
    /// </summary>
    public static void ExtractEntry(string zipPath, string entrySuffix, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(entrySuffix, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            throw new FileNotFoundException($"{entrySuffix.TrimStart('/')} not found inside the download.");

        var tmp = destination + ".partial";
        entry.ExtractToFile(tmp, overwrite: true);
        File.Move(tmp, destination, overwrite: true);
    }
}
