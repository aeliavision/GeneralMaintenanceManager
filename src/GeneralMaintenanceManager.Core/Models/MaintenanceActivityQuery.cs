using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.Core.Models;

/// <summary>Bounded query over immutable annual Maintenance audit events.</summary>
public sealed record MaintenanceActivityQuery(
    int? Year = null,
    DateTimeOffset? FromInclusive = null,
    DateTimeOffset? ToInclusive = null,
    string ActivityType = "",
    string Site = "",
    string Location = "",
    string MaintenanceType = "",
    string PerformedBy = "",
    string SearchText = "",
    int PageSize = 100,
    MaintenanceActivityPageCursor? Cursor = null);

public sealed record MaintenanceActivityPageCursor(
    long OccurredAtUtcTicks,
    int ReferenceYear,
    int OffsetAtTimestamp);

public sealed record MaintenanceActivityEvent(MaintenanceRecord Record, MaintenanceActivity Activity);

public sealed record MaintenanceActivityPage(
    IReadOnlyList<MaintenanceActivityEvent> Items,
    MaintenanceActivityPageCursor? NextCursor,
    bool HasMore);
