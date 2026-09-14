using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class MaintenanceRecordDetailsDialogViewModel
{
    public MaintenanceRecordDetailsDialogViewModel(
        MaintenanceRecord record,
        IReadOnlyList<MaintenanceActivity> activities,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(localization);
        Record = record;
        MaintenanceType = MaintenanceTextPresentation.LocalizeMaintenanceType(record.MaintenanceType, localization);
        Activities = activities.Select(activity => new ActivityDisplayRow(
            activity.OccurredAtUtc.ToLocalTime().ToString("g", localization.CurrentCulture),
            MaintenanceTextPresentation.LocalizeMaintenanceActivityType(activity.ActivityType, localization),
            MaintenanceTextPresentation.FormatMaintenanceActivityDescription(activity, localization),
            activity.ChangedBy)).ToArray();
        Date = record.OccurredAt.ToString("dd MMM yyyy HH:mm", localization.CurrentCulture);
        Cost = record.Cost.HasValue ? record.Cost.Value.ToString("N2", localization.CurrentCulture) : string.Empty;
        SourceReference = string.IsNullOrWhiteSpace(record.WorkOrderNumberSnapshot)
            ? localization.GetString("DirectMaintenance")
            : record.WorkOrderNumberSnapshot;
        AssetReference = !string.IsNullOrWhiteSpace(record.AssetNumberSnapshot)
            ? record.AssetNumberSnapshot
            : !string.IsNullOrWhiteSpace(record.AssetNameSnapshot)
                ? record.AssetNameSnapshot
                : record.AssetId.HasValue ? localization.GetString("UnassignedAsset") : localization.GetString("NoAsset");
        VoidStatus = record.IsInvalid
            ? localization.Format("MaintenanceInvalidReasonFormat", record.InvalidReason)
            : localization.GetString("MaintenanceActiveRecord");
    }

    public MaintenanceRecord Record { get; }
    public IReadOnlyList<ActivityDisplayRow> Activities { get; }
    public string MaintenanceType { get; }
    public string Date { get; }
    public string Cost { get; }
    public string SourceReference { get; }
    public string AssetReference { get; }
    public string VoidStatus { get; }
    public bool CanModify => !Record.IsInvalid;
}
