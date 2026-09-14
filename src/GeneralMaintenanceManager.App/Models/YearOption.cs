namespace GeneralMaintenanceManager.App.Models;

public sealed record YearOption(int? Year, string DisplayName)
{
    public override string ToString() => DisplayName;
}
