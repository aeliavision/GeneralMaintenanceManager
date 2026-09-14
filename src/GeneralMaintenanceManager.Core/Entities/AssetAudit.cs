namespace GeneralMaintenanceManager.Core.Entities;

public sealed class AssetAudit
{
    public Guid AuditId { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Changes { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTimeOffset ChangedAtUtc { get; set; }
}
