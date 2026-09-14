namespace GeneralMaintenanceManager.Core.Entities;

/// <summary>
/// Immutable annual audit history for a canonical MaintenanceRecord.
/// </summary>
public sealed class MaintenanceActivity
{
    public Guid MaintenanceActivityId { get; set; } = Guid.NewGuid();
    public Guid MaintenanceRecordId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public long OccurredAtUtcTicks { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Changes { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
}

public static class MaintenanceActivityTypes
{
    public const string Created = "Created";
    public const string Edited = "Edited";
    public const string MarkedInvalid = "MarkedInvalid";
}
