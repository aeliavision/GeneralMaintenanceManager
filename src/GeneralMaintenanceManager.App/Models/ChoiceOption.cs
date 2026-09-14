namespace GeneralMaintenanceManager.App.Models;

public sealed record ChoiceOption<T>(T Value, string Display)
{
    // ModernComboBoxStyle binds to DisplayName so every option model uses one
    // consistent presentation contract. Keep Display for existing callers.
    public string DisplayName => Display;

    public override string ToString() => Display;
}

public sealed record AssetChoice(
    Guid? AssetId,
    string Display,
    string Site,
    string Location,
    string Category,
    string Condition,
    string OperationalStatus)
{
    public string DisplayName => Display;

    public override string ToString() => Display;
}
