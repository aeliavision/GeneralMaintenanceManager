namespace GeneralMaintenanceManager.Core.Entities;

public sealed class Asset
{
    public Guid AssetId { get; set; } = Guid.NewGuid();
    public string AssetNumber { get; set; } = string.Empty;
    public string ImportedAssetNumber { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string AssetCategory { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = "In Service";
    public string TechnicalSpecification { get; set; } = string.Empty;
    public DateOnly? PurchaseDate { get; set; }
    public decimal? PurchasePrice { get; set; }
    public DateOnly? InstallationDate { get; set; }
    public DateOnly? WarrantyExpiryDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public int SourceRow { get; set; }
    public bool IsArchived { get; set; }
    public Guid? MergedIntoAssetId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
