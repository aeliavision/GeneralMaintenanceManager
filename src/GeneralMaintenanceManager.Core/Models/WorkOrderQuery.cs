using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

/// <summary>Bounded SQL-side Work Order query for operational screens and reports.</summary>
public sealed record WorkOrderQuery(
    WorkOrderStatus? Status = null,
    WorkOrderPriority? Priority = null,
    string AssignedTo = "",
    string PerformedBy = "",
    string Site = "",
    string Location = "",
    DateOnly? DueFromInclusive = null,
    DateOnly? DueToInclusive = null,
    DateTimeOffset? ActivityFromInclusive = null,
    DateTimeOffset? ActivityToInclusive = null,
    int? ReferenceYear = null,
    bool? IsClosed = null,
    string SearchText = "",
    int PageSize = 100,
    WorkOrderPageCursor? Cursor = null);

/// <summary>Stable keyset cursor matching Work Order business ordering.</summary>
public sealed record WorkOrderPageCursor(
    bool IsClosed,
    WorkOrderPriority Priority,
    int SortDueDateOrdinal,
    long ReportedAtUtcTicks,
    long ReferenceSequence);

public sealed record WorkOrderPage(
    IReadOnlyList<WorkOrder> Items,
    WorkOrderPageCursor? NextCursor,
    bool HasMore);

public sealed record WorkOrderDashboardMetrics(long OpenCount, long UrgentOpenCount, long OverdueOpenCount);
