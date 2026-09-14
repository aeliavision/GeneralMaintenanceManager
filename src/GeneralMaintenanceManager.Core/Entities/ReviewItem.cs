namespace GeneralMaintenanceManager.Core.Entities;

public sealed class ReviewItem
{
    public Guid ReviewId { get; set; } = Guid.NewGuid();
    public string ReviewType { get; set; } = string.Empty;
    public Guid AssetIdA { get; set; }
    public Guid? AssetIdB { get; set; }
    public string Reasons { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Resolution { get; set; } = string.Empty;
    public string ResolvedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
