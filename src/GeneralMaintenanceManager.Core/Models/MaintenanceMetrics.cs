namespace GeneralMaintenanceManager.Core.Models;

public sealed record MaintenanceAggregate(long Count, decimal TotalCost);

public sealed record MaintenanceMetric(string Label, long Count, decimal TotalCost);

public sealed record MaintenanceDashboardSummaryVerification(
    int ReferenceYear,
    bool IsConsistent,
    long LocationMismatchCount,
    long MaintenanceTypeMismatchCount);

public sealed record MaintenanceDashboardSummary(
    int ReferenceYear,
    long MonthCount,
    decimal MonthCost,
    IReadOnlyList<Entities.MaintenanceRecord> RecentRecords,
    IReadOnlyList<MaintenanceMetric> TopLocations,
    IReadOnlyList<MaintenanceMetric> TopMaintenanceTypes);
