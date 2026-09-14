using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Entities;

public sealed class WorkOrder
{
    public Guid WorkOrderId { get; set; } = Guid.NewGuid();
    public string WorkOrderNumber { get; set; } = string.Empty;
    // Permanent/query projection derived from WO-YYYY-NNNNNN. Kept immutable with the reference.
    public int ReferenceYear { get; set; }
    public long ReferenceSequence { get; set; }
    public Guid? AssetId { get; set; }
    public string AssetNumber { get; set; } = string.Empty;
    public Guid? MaintenancePlanId { get; set; }
    public Guid? GeneratedMaintenanceRecordId { get; set; }
    public string GeneratedMaintenanceNumber { get; set; } = string.Empty;
    public int? GeneratedMaintenanceYear { get; set; }
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string MaintenanceCategory { get; set; } = string.Empty;
    public MaintenanceType MaintenanceType { get; set; } = MaintenanceType.GeneralMaintenance;
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
    public string ProblemDescription { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset ReportedAtUtc { get; set; }
    public long ReportedAtUtcTicks { get; set; }
    public DateOnly? DueDate { get; set; }
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.New;
    public string AssignedTo { get; set; } = string.Empty;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string WorkPerformed { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public decimal? Cost { get; set; }
    public decimal? DowntimeHours { get; set; }
    public string MaterialsOrPartsNotes { get; set; } = string.Empty;
    public string ConditionAfter { get; set; } = string.Empty;
    public string OperationalStatusAfter { get; set; } = string.Empty;
    public string CompletionNotes { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset? LastModifiedAtUtc { get; set; }
    /// <summary>Latest operational activity timestamp used by bounded report queries.</summary>
    public long ActivityAtUtcTicks { get; set; }
    /// <summary>Materialized closed-state projection for stable keyset ordering.</summary>
    public bool IsClosed { get; set; }
    /// <summary>DateOnly day-number used for deterministic SQL ordering; null due dates sort last.</summary>
    public int SortDueDateOrdinal { get; set; } = int.MaxValue;
    public string LastModifiedBy { get; set; } = string.Empty;
}
