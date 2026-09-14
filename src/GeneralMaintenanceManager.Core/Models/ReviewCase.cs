using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.Core.Models;

public sealed record ReviewCase(
    Guid ReviewId,
    string ReviewType,
    string Reasons,
    Asset AssetA,
    Asset? AssetB,
    string Status);
