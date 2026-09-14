using System.Globalization;

namespace GeneralMaintenanceManager.App.Services;

public interface ILocalizationService
{
    public string CurrentLanguageCode { get; }

    public CultureInfo CurrentCulture { get; }

    public event EventHandler? LanguageChanged;

    public string GetString(string key);

    public string Format(string key, params object?[] arguments);

    public string LocalizeCondition(string? condition);

    public void SetLanguage(string languageCode);
}
