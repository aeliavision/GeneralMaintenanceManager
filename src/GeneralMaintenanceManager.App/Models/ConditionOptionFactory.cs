using GeneralMaintenanceManager.App.Services;

namespace GeneralMaintenanceManager.App.Models;

internal static class ConditionOptionFactory
{
    private static readonly string[] StandardConditions =
    [
        "Excellent",
        "Very Good",
        "Good",
        "Fair",
        "Poor"
    ];

    public static IReadOnlyList<ConditionOption> Create(
        ILocalizationService localization,
        string? currentCondition = null)
    {
        ArgumentNullException.ThrowIfNull(localization);

        var options = StandardConditions
            .Select(value => new ConditionOption(value, localization.LocalizeCondition(value)))
            .ToList();

        var current = currentCondition?.Trim() ?? string.Empty;
        if (current.Length > 0
            && !options.Any(option => option.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            options.Insert(0, new ConditionOption(current, current));
        }

        return options;
    }
}
