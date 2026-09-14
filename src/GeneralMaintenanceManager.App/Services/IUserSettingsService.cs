namespace GeneralMaintenanceManager.App.Services;

public interface IUserSettingsService
{
    public string LoadLanguageCode();

    public void SaveLanguageCode(string languageCode);

    public bool LoadWorkOrdersEnabled();

    public void SaveWorkOrdersEnabled(bool enabled);

    public bool LoadPreventiveMaintenanceEnabled();

    public void SavePreventiveMaintenanceEnabled(bool enabled);

    public bool LoadAssetTrackingEnabled();

    public void SaveAssetTrackingEnabled(bool enabled);

    public IReadOnlyList<string> LoadCustomMaintenanceTypes();

    public void SaveCustomMaintenanceTypes(IReadOnlyCollection<string> maintenanceTypes);

    public PrintSettings LoadPrintSettings();

    public void SavePrintSettings(PrintSettings settings);

    public string ImportPrintLogo(string sourcePath);
}
