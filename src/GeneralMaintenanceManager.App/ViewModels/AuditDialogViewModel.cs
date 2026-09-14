using System.Collections.ObjectModel;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class AuditDialogViewModel
{
    public AuditDialogViewModel(Asset? asset, MaintenanceRecord record, IReadOnlyList<MaintenanceActivity> activities, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(localization);
        AssetLabel = asset is not null
            ? (string.IsNullOrWhiteSpace(asset.AssetNumber) ? localization.GetString("UnassignedAsset") : $"#{asset.AssetNumber}")
            : (!string.IsNullOrWhiteSpace(record.AssetNumberSnapshot) ? $"#{record.AssetNumberSnapshot}" : localization.GetString("NoAsset"));
        AssetName = asset?.AssetName ?? record.AssetNameSnapshot;
        RecordTitle = record.Subject;
        foreach (var activity in activities.OrderByDescending(item => item.OccurredAtUtc))
            Entries.Add(new AuditEntryViewModel(activity, localization));
    }

    public string AssetLabel { get; }
    public string AssetName { get; }
    public string RecordTitle { get; }
    public ObservableCollection<AuditEntryViewModel> Entries { get; } = [];
}

public sealed class AuditEntryViewModel
{
    public AuditEntryViewModel(MaintenanceActivity activity, ILocalizationService localization)
    {
        ChangedAt = activity.OccurredAtUtc.ToLocalTime().ToString("g", localization.CurrentCulture);
        ChangedBy = string.IsNullOrWhiteSpace(activity.ChangedBy) ? "-" : activity.ChangedBy;
        Reason = activity.Reason;
        var displayDescription = MaintenanceTextPresentation.FormatMaintenanceActivityDescription(activity, localization);
        if (!string.IsNullOrWhiteSpace(displayDescription)
            && !string.Equals(activity.ActivityType, MaintenanceActivityTypes.MarkedInvalid, StringComparison.Ordinal))
        {
            Changes.Add(new AuditChangeViewModel(localization.GetString("Description"), string.Empty, displayDescription));
        }
    }

    public string ChangedAt { get; }
    public string ChangedBy { get; }
    public string Reason { get; }
    public ObservableCollection<AuditChangeViewModel> Changes { get; } = [];
}

public sealed record AuditChangeViewModel(string Field, string Before, string After);
