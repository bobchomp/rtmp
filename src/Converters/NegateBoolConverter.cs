using System.Globalization;
using System.Windows.Data;

namespace RTMPProjector.ViewModels;

/// <summary>
/// Inverts a boolean — used to disable the server button while busy.
/// Exposed as a static instance so it can be referenced via x:Static in XAML.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public class NegateBoolConverter : IValueConverter
{
    public static readonly NegateBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
