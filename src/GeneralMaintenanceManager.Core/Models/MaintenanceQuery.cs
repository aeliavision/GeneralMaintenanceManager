namespace GeneralMaintenanceManager.Core.Models;

/// <summary>
/// Bounded database-side query for canonical annual Maintenance records.
/// </summary>
public sealed record MaintenanceQuery(
    int? Year = null,
    DateTimeOffset? FromInclusive = null,
    DateTimeOffset? ToInclusive = null,
    Guid? AssetId = null,
    string Site = "",
    string Location = "",
    string MaintenanceType = "",
    string PerformedBy = "",
    string SearchText = "",
    bool IncludeInvalid = true,
    int PageSize = 100,
    MaintenancePageCursor? Cursor = null);

public sealed record MaintenancePageCursor(long OccurredAtUtcTicks, long CreatedAtUtcTicks, int ReferenceYear, long ReferenceSequence);

public sealed record MaintenancePage(
    IReadOnlyList<Entities.MaintenanceRecord> Items,
    MaintenancePageCursor? NextCursor,
    bool HasMore);
