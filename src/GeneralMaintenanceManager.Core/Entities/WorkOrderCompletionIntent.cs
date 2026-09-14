namespace GeneralMaintenanceManager.Core.Entities;

/// <summary>
/// Durable master-database coordination record for the cross-database Work Order completion workflow.
/// The reserved Maintenance identity and completion payload survive interruption so retries converge
/// on the same canonical annual Maintenance record.
/// </summary>
public sealed class WorkOrderCompletionIntent
{
    public Guid WorkOrderCompletionIntentId { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid MaintenanceRecordId { get; set; }
    public string MaintenanceNumber { get; set; } = string.Empty;
    public int ReferenceYear { get; set; }
    public long ReferenceSequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string MaintenanceType { get; set; } = string.Empty;
    public Guid? AssetId { get; set; }
    public string AssetNumberSnapshot { get; set; } = string.Empty;
    public string AssetNameSnapshot { get; set; } = string.Empty;
    public string WorkOrderNumberSnapshot { get; set; } = string.Empty;
    public string WorkPerformed { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public decimal? Cost { get; set; }
    public decimal? DowntimeHours { get; set; }
    public string MaterialsOrPartsNotes { get; set; } = string.Empty;
    public string ConditionAfter { get; set; } = string.Empty;
    public string OperationalStatusAfter { get; set; } = string.Empty;
    public string CompletionNotes { get; set; } = string.Empty;
    public string MaintenanceNotes { get; set; } = string.Empty;
    public string State { get; set; } = WorkOrderCompletionIntentStates.Reserved;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? AnnualWrittenAtUtc { get; set; }
    public DateTimeOffset? FinalizedAtUtc { get; set; }
}

public static class WorkOrderCompletionIntentStates
{
    public const string Reserved = "Reserved";
    public const string AnnualWritten = "AnnualWritten";
    public const string Finalized = "Finalized";
}
