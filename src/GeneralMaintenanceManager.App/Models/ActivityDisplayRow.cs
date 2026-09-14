namespace GeneralMaintenanceManager.App.Models;

public sealed record ActivityDisplayRow(
    string DateText,
    string ActivityText,
    string Description,
    string ChangedBy);
