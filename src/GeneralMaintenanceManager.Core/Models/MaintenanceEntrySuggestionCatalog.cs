namespace GeneralMaintenanceManager.Core.Models;

/// <summary>
/// Complete distinct values used to seed Maintenance entry suggestions without loading
/// bounded operational record windows into memory.
/// </summary>
public sealed record MaintenanceEntrySuggestionCatalog(
    IReadOnlyList<string> Sites,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Subjects)
{
    public static MaintenanceEntrySuggestionCatalog Empty { get; } = new([], [], []);
}
