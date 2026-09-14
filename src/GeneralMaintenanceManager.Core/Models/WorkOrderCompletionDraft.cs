namespace GeneralMaintenanceManager.Core.Models;

public sealed record WorkOrderCompletionDraft(
    Guid WorkOrderId,
    string WorkPerformed,
    string PerformedBy,
    decimal? Cost,
    decimal? DowntimeHours,
    string MaterialsOrPartsNotes,
    string ConditionAfter,
    string OperationalStatusAfter,
    string CompletionNotes);
