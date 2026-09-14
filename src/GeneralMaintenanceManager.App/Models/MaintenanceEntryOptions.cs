using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.Models;

public sealed record MaintenanceEntryOptions(
    IReadOnlyList<string> Sites,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> MaintenanceTypes,
    IReadOnlyList<Asset> Assets,
    bool AssetTrackingEnabled);
