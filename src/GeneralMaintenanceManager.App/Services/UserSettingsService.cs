using System.IO;
using System.Text;
using System.Text.Json;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.Core.Abstractions;

namespace GeneralMaintenanceManager.App.Services;

public sealed class UserSettingsService(IDataLocationService dataLocationService) : IUserSettingsService
{
    private const string SettingsFileName = "UserSettings.json";
    private const string PrintBrandingDirectoryName = "PrintBranding";
    private const int MaxHeaderLength = 120;
    private const int MaxFooterLength = 240;
    private const int MaxCenterTextLength = 160;
    private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public string LoadLanguageCode()
    {
        var settings = ReadSettings();
        return string.Equals(settings.Language, "ar", StringComparison.OrdinalIgnoreCase)
            ? "ar"
            : "en";
    }

    public void SaveLanguageCode(string languageCode)
    {
        var settings = ReadSettings();
        settings.Language = string.Equals(languageCode, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
        WriteSettings(settings);
    }

    public bool LoadWorkOrdersEnabled() => ReadSettings().EnableWorkOrders;

    public void SaveWorkOrdersEnabled(bool enabled)
    {
        var settings = ReadSettings();
        settings.EnableWorkOrders = enabled;
        WriteSettings(settings);
    }

    public bool LoadPreventiveMaintenanceEnabled() => ReadSettings().EnablePreventiveMaintenance;

    public void SavePreventiveMaintenanceEnabled(bool enabled)
    {
        var settings = ReadSettings();
        settings.EnablePreventiveMaintenance = enabled;
        WriteSettings(settings);
    }

    public bool LoadAssetTrackingEnabled() => ReadSettings().EnableAssetTracking;

    public void SaveAssetTrackingEnabled(bool enabled)
    {
        var settings = ReadSettings();
        settings.EnableAssetTracking = enabled;
        WriteSettings(settings);
    }

    public IReadOnlyList<string> LoadCustomMaintenanceTypes()
    {
        var settings = ReadSettings();
        return NormalizeMaintenanceTypes(settings.CustomMaintenanceTypes);
    }

    public void SaveCustomMaintenanceTypes(IReadOnlyCollection<string> maintenanceTypes)
    {
        ArgumentNullException.ThrowIfNull(maintenanceTypes);
        var settings = ReadSettings();
        settings.CustomMaintenanceTypes = NormalizeMaintenanceTypes(maintenanceTypes).ToList();
        WriteSettings(settings);
    }


    public PrintSettings LoadPrintSettings()
    {
        try
        {
            var settings = ReadSettings();
            var persisted = settings.Print ?? new PersistedPrintSettings();
            return new PrintSettings(
                persisted.AutoPrintWorkOrderActions,
                NormalizeText(persisted.HeaderText, MaxHeaderLength),
                NormalizeText(persisted.FooterText, MaxFooterLength),
                Enum.TryParse<PrintCenterBrandingMode>(persisted.CenterBrandingMode, true, out var mode) ? mode : PrintCenterBrandingMode.Logo,
                NormalizeText(persisted.CenterText, MaxCenterTextLength),
                ResolveManagedLogoPath(persisted.LogoFileName),
                persisted.WorkOrderLayout ?? WorkOrderPrintLayout.Default,
                persisted.MaintenanceLayout ?? MaintenancePrintLayout.Default,
                persisted.ListReportLayout ?? ListReportPrintLayout.Default);
        }
        catch
        {
            return PrintSettings.Default;
        }
    }

    public void SavePrintSettings(PrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var persistedSettings = ReadSettings();
        persistedSettings.Print = new PersistedPrintSettings
        {
            AutoPrintWorkOrderActions = settings.AutoPrintWorkOrderActions,
            HeaderText = NormalizeText(settings.HeaderText, MaxHeaderLength),
            FooterText = NormalizeText(settings.FooterText, MaxFooterLength),
            CenterBrandingMode = settings.CenterBrandingMode.ToString(),
            CenterText = NormalizeText(settings.CenterText, MaxCenterTextLength),
            LogoFileName = GetManagedLogoFileName(settings.LogoPath),
            WorkOrderLayout = settings.WorkOrderLayout,
            MaintenanceLayout = settings.MaintenanceLayout,
            ListReportLayout = settings.ListReportLayout
        };
        WriteSettings(persistedSettings);
    }

    public string ImportPrintLogo(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("The selected logo file does not exist.", fullSourcePath);
        var extension = Path.GetExtension(fullSourcePath);
        if (!AllowedLogoExtensions.Contains(extension)) throw new InvalidOperationException("Only PNG and JPEG print logos are supported.");
        var directory = GetPrintBrandingDirectory();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"PrintLogo-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        File.Copy(fullSourcePath, destination, overwrite: false);
        return destination;
    }

    private string GetPrintBrandingDirectory() => Path.Combine(dataLocationService.DataDirectory, PrintBrandingDirectoryName);

    private string? ResolveManagedLogoPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return null;
        var path = Path.Combine(GetPrintBrandingDirectory(), fileName);
        return File.Exists(path) ? path : null;
    }

    private string? GetManagedLogoFileName(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        var full = Path.GetFullPath(logoPath);
        var root = Path.GetFullPath(GetPrintBrandingDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("The print logo must be imported through General Maintenance Manager before saving print settings.");
        return Path.GetFileName(full);
    }

    private static string NormalizeText(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private UserSettings ReadSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new UserSettings();
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<UserSettings>(json, SerializerOptions) ?? new UserSettings();
        }
        catch
        {
            // Preferences must never prevent the maintenance application from starting.
            return new UserSettings();
        }
    }

    private void WriteSettings(UserSettings settings)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(dataLocationService.DataDirectory);
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var settingsPath = GetSettingsPath();
            temporaryPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";

            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, settingsPath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("User settings could not be saved atomically.", ex);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }


    private static IReadOnlyList<string> NormalizeMaintenanceTypes(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = CustomMaintenanceTypeStorage.Normalize(value);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
            if (result.Count >= 50)
            {
                break;
            }
        }

        return result;
    }

    private string GetSettingsPath() => Path.Combine(dataLocationService.DataDirectory, SettingsFileName);

    private sealed class UserSettings
    {
        public string Language { get; set; } = "en";

        // Work Orders and Preventive Maintenance are core maintenance workflows and default ON.
        public bool EnableWorkOrders { get; set; } = true;

        public bool EnablePreventiveMaintenance { get; set; } = true;

        // Asset Tracking is an advanced compatibility feature and is OFF by default.
        public bool EnableAssetTracking { get; set; }

        public List<string> CustomMaintenanceTypes { get; set; } = [];

        public PersistedPrintSettings? Print { get; set; } = new();
    }

    private sealed class PersistedPrintSettings
    {
        public bool AutoPrintWorkOrderActions { get; set; }
        public string HeaderText { get; set; } = string.Empty;
        public string FooterText { get; set; } = string.Empty;
        public string CenterBrandingMode { get; set; } = PrintCenterBrandingMode.Logo.ToString();
        public string CenterText { get; set; } = string.Empty;
        public string? LogoFileName { get; set; }
        public WorkOrderPrintLayout? WorkOrderLayout { get; set; } = WorkOrderPrintLayout.Default;
        public MaintenancePrintLayout? MaintenanceLayout { get; set; } = MaintenancePrintLayout.Default;
        public ListReportPrintLayout? ListReportLayout { get; set; } = ListReportPrintLayout.Default;
    }
}
