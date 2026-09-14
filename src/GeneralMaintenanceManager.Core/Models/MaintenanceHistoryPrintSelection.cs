using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.Core.Models;

/// <summary>
/// Bounded selection used for printable asset history. IsTruncated is true when more
/// canonical Maintenance records exist than the configured print safety limit.
/// </summary>
public sealed record MaintenanceHistoryPrintSelection(
    IReadOnlyList<MaintenanceRecord> Items,
    bool IsTruncated);
