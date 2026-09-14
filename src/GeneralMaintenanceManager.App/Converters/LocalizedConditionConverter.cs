using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Converters;

public sealed class LocalizedConditionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var condition = value?.ToString()?.Trim() ?? string.Empty;
        var resourceKey = ConditionResourceKeys.GetResourceKey(condition);

        return resourceKey is not null
            && Application.Current?.TryFindResource(resourceKey) is string localized
                ? localized
                : condition;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
