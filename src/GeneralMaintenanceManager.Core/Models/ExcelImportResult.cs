namespace GeneralMaintenanceManager.Core.Models;

public sealed record ExcelImportResult(
    int Added,
    int Updated,
    int Unchanged,
    int MissingAssetNumber,
    int DuplicateAssetNumber,
    int InvalidRows,
    IReadOnlyList<ImportIssue> Issues,
    string? LogFilePath)
{
    public int Processed => Added + Updated + Unchanged;
}
