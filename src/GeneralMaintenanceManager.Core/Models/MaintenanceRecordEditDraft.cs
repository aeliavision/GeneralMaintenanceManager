namespace GeneralMaintenanceManager.Core.Models;

public sealed record MaintenanceRecordEditDraft(
    DateTimeOffset OccurredAt,
    string Site,
    string Location,
    string Subject,
    string MaintenanceType,
    string WorkPerformed,
    string PerformedBy,
    decimal? Cost,
    string Notes);
