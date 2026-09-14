namespace GeneralMaintenanceManager.Core.Models;

public sealed record MaintenanceRecordDraft(
    DateTimeOffset OccurredAt,
    string Site,
    string Location,
    string Subject,
    string MaintenanceType,
    string WorkPerformed,
    string PerformedBy,
    decimal? Cost,
    string Notes,
    Guid? AssetId = null,
    Guid? GeneratedByWorkOrderId = null,
    string WorkOrderNumberSnapshot = "");
