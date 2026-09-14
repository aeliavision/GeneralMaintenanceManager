namespace GeneralMaintenanceManager.App.Models;

public sealed record SummaryMetricItem(string Label, string Value, string SecondaryText = "")
{
    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText);
}
