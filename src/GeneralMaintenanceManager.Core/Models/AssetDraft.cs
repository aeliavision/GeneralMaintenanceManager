namespace GeneralMaintenanceManager.Core.Models;

public sealed record AssetDraft(
    string AssetNumber,
    string Site,
    string Location,
    string AssetName,
    string AssetCategory,
    string Manufacturer,
    string Country,
    string Model,
    string SerialNumber,
    string Condition,
    string OperationalStatus,
    string TechnicalSpecification,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiryDate,
    string Notes)
{
}
