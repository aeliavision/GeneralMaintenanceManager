namespace GeneralMaintenanceManager.Core.Models;

public sealed record ImportIssue(
    string Worksheet,
    int RowNumber,
    string AssetNumber,
    string Reason);
