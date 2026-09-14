using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace GeneralMaintenanceManager.App.Converters;

public sealed class SingleLineTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (parameter is not null && int.TryParse(parameter.ToString(), out var maxLength) && maxLength > 0 && text.Length > maxLength)
        {
            text = text[..Math.Max(0, maxLength - 1)].TrimEnd() + "…";
        }

        return text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
