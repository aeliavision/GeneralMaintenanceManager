using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace GeneralMaintenanceManager.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    private const string EnglishCode = "en";
    private const string ArabicCode = "ar";
    private const string LanguageDictionaryPrefix = "Localization/Strings.";

    private string _currentLanguageCode = EnglishCode;
    private CultureInfo _currentCulture = CultureInfo.GetCultureInfo("en-US");

    public string CurrentLanguageCode => _currentLanguageCode;

    public CultureInfo CurrentCulture => _currentCulture;

    public event EventHandler? LanguageChanged;

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var value = Application.Current?.TryFindResource(key) as string;
        return value ?? key;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CurrentCulture, GetString(key), arguments);

    public string LocalizeCondition(string? condition)
    {
        var normalized = condition?.Trim() ?? string.Empty;
        var resourceKey = ConditionResourceKeys.GetResourceKey(normalized);
        return resourceKey is null ? normalized : GetString(resourceKey);
    }

    public void SetLanguage(string languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        var culture = normalized == ArabicCode
            ? CultureInfo.GetCultureInfo("ar-LB")
            : CultureInfo.GetCultureInfo("en-US");

        _currentLanguageCode = normalized;
        _currentCulture = culture;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is not null)
        {
            var newDictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/GeneralMaintenanceManager;component/{LanguageDictionaryPrefix}{normalized}.xaml", UriKind.Absolute)
            };

            var dictionaries = app.Resources.MergedDictionaries;
            var existingIndex = -1;

            for (var index = 0; index < dictionaries.Count; index++)
            {
                var source = dictionaries[index].Source?.OriginalString;
                if (source?.Contains(LanguageDictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                dictionaries[existingIndex] = newDictionary;
            }
            else
            {
                dictionaries.Add(newDictionary);
            }

            app.Resources["AppFlowDirection"] = culture.TextInfo.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;

            foreach (Window window in app.Windows)
            {
                window.FlowDirection = culture.TextInfo.IsRightToLeft
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                window.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            }
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeLanguageCode(string? languageCode) =>
        string.Equals(languageCode?.Trim(), ArabicCode, StringComparison.OrdinalIgnoreCase)
            ? ArabicCode
            : EnglishCode;
}

internal static class ConditionResourceKeys
{
    public static string? GetResourceKey(string condition)
    {
        if (condition.Equals("Excellent", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionExcellent";
        }

        if (condition.Equals("Very Good", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionVeryGood";
        }

        if (condition.Equals("Good", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionGood";
        }

        if (condition.Equals("Fair", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionFair";
        }

        if (condition.Equals("Poor", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionPoor";
        }

        if (condition.Equals("Under Maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionUnderMaintenance";
        }

        if (condition.Equals("Out of Service", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionOutOfService";
        }

        if (condition.Equals("Discarded", StringComparison.OrdinalIgnoreCase))
        {
            return "ConditionDiscarded";
        }

        return null;
    }
}
