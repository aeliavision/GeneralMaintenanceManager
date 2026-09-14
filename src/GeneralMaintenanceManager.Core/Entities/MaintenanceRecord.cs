namespace GeneralMaintenanceManager.Core.Entities;

/// <summary>
/// The single canonical record of maintenance work that actually happened.
/// A record is stored only in the annual database identified by ReferenceYear.
/// Asset linkage is optional by design.
/// </summary>
public sealed class MaintenanceRecord
{
    public Guid MaintenanceRecordId { get; set; } = Guid.NewGuid();
    public string MaintenanceNumber { get; set; } = string.Empty;
    public int ReferenceYear { get; set; }
    public long ReferenceSequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long OccurredAtUtcTicks { get; set; }
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string MaintenanceType { get; set; } = string.Empty;
    public string WorkPerformed { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public decimal? Cost { get; set; }
    /// <summary>Exact downtime value; persisted as whole minutes by the Infrastructure layer.</summary>
    public decimal? DowntimeHours { get; set; }
    public string Notes { get; set; } = string.Empty;

    // Cross-database references are validated by the service layer. Snapshots keep
    // historical presentation readable if operational master data later changes.
    public Guid? AssetId { get; set; }
    public string AssetNumberSnapshot { get; set; } = string.Empty;
    public string AssetNameSnapshot { get; set; } = string.Empty;
    public Guid? GeneratedByWorkOrderId { get; set; }
    public string WorkOrderNumberSnapshot { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Revision { get; set; } = 1;

    // Permanent history is invalidated, never deleted.
    public bool IsInvalid { get; set; }
    public string InvalidReason { get; set; } = string.Empty;
}
