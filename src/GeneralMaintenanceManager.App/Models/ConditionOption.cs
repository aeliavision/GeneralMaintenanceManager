namespace GeneralMaintenanceManager.App.Models;

public sealed record ConditionOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
