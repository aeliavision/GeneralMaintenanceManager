using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Models;

internal static class AssetClassificationOptionFactory
{
    private static readonly (string Value, string ResourceKey)[] Categories =
    [
        ("General", "AssetCategoryGeneral"),
        ("Generator", "AssetCategoryGenerator"),
        ("HVAC", "AssetCategoryHvac"),
        ("Electrical", "AssetCategoryElectrical"),
        ("Plumbing", "AssetCategoryPlumbing"),
        ("Vehicle", "AssetCategoryVehicle"),
        ("Machinery", "AssetCategoryMachinery"),
        ("IT Equipment", "AssetCategoryIt"),
        ("Building", "AssetCategoryBuilding"),
        ("Medical Equipment", "AssetCategoryMedical"),
        ("Tool", "AssetCategoryTool"),
        ("Other", "AssetCategoryOther")
    ];

    private static readonly (string Value, string ResourceKey)[] OperationalStatuses =
    [
        ("In Service", "StatusInService"),
        ("Standby", "StatusStandby"),
        ("Under Maintenance", "StatusUnderMaintenance"),
        ("Out of Service", "StatusOutOfService"),
        ("Retired", "StatusRetired"),
        ("Disposed", "StatusDisposed")
    ];

    public static IReadOnlyList<ChoiceOption<string>> CreateCategories(
        ILocalizationService localization,
        string? currentValue = null) => Create(localization, Categories, currentValue);

    public static IReadOnlyList<ChoiceOption<string>> CreateOperationalStatuses(
        ILocalizationService localization,
        string? currentValue = null) => Create(localization, OperationalStatuses, currentValue);

    private static List<ChoiceOption<string>> Create(
        ILocalizationService localization,
        IReadOnlyList<(string Value, string ResourceKey)> standard,
        string? currentValue)
    {
        ArgumentNullException.ThrowIfNull(localization);

        var options = standard
            .Select(item => new ChoiceOption<string>(item.Value, localization.GetString(item.ResourceKey)))
            .ToList();

        var current = currentValue?.Trim() ?? string.Empty;
        if (current.Length > 0 && !options.Any(option => option.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            options.Insert(0, new ChoiceOption<string>(current, current));
        }

        return options;
    }
}
