namespace GeneralMaintenanceManager.Core.Models;

/// <summary>
/// Reserved identity + immutable Work Order completion snapshot used to create the canonical
/// annual Maintenance record idempotently after the master completion intent is committed.
/// </summary>
public sealed record WorkOrderGeneratedMaintenanceDraft(
    Guid WorkOrderId,
    Guid MaintenanceRecordId,
    string MaintenanceNumber,
    int ReferenceYear,
    long ReferenceSequence,
    DateTimeOffset OccurredAt,
    string Site,
    string Location,
    string Subject,
    string MaintenanceType,
    string WorkPerformed,
    string PerformedBy,
    decimal? Cost,
    string Notes,
    Guid? AssetId,
    string AssetNumberSnapshot,
    string AssetNameSnapshot,
    string WorkOrderNumberSnapshot,
    decimal? DowntimeHours = null);
