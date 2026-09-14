using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class WorkOrderDetailsDialogViewModel
{
    public WorkOrderDetailsDialogViewModel(
        WorkOrder workOrder,
        Asset? asset,
        MaintenanceRecord? generatedMaintenanceRecord,
        IReadOnlyList<ActivityHistoryEntry> activities,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(localization);
        WorkOrder = workOrder;
        Activities = activities.Select(activity => new ActivityDisplayRow(
            activity.OccurredAtUtc.ToLocalTime().ToString("g", localization.CurrentCulture),
            MaintenanceTextPresentation.LocalizeActivityType(activity.ActivityType, localization),
            activity.Description,
            activity.ChangedBy)).ToArray();
        GeneratedMaintenanceRecord = generatedMaintenanceRecord;

        WorkOrderNumber = Display(workOrder.WorkOrderNumber);
        Title = Display(workOrder.Title);
        AssetName = asset is null ? localization.GetString("GeneralMaintenanceRecord") : Display(asset.AssetName);
        AssetNumber = asset is null
            ? localization.GetString("NoAsset")
            : string.IsNullOrWhiteSpace(asset.AssetNumber) ? localization.GetString("UnassignedAsset") : $"#{asset.AssetNumber}";
        Site = Display(workOrder.Site);
        Location = Display(workOrder.Location);
        MaintenanceTypeText = LocalizeMaintenanceType(workOrder.MaintenanceType, localization);
        Category = Display(workOrder.MaintenanceCategory);
        Priority = MaintenanceTextPresentation.LocalizeWorkOrderPriority(workOrder.Priority, localization);
        Status = MaintenanceTextPresentation.LocalizeWorkOrderStatus(workOrder.Status, localization);
        Problem = Display(workOrder.ProblemDescription);
        RequestedBy = Display(workOrder.RequestedBy);
        ReportedAt = FormatDateTime(workOrder.ReportedAtUtc, localization);
        DueDate = workOrder.DueDate?.ToString("d", localization.CurrentCulture) ?? "-";
        AssignedTo = Display(workOrder.AssignedTo);
        StartedAt = FormatDateTime(workOrder.StartedAtUtc, localization);
        CompletedAt = FormatDateTime(workOrder.CompletedAtUtc, localization);
        WorkPerformed = Display(workOrder.WorkPerformed);
        PerformedBy = Display(workOrder.PerformedBy);
        Cost = workOrder.Cost?.ToString("N2", localization.CurrentCulture) ?? "-";
        DowntimeHours = workOrder.DowntimeHours?.ToString("N2", localization.CurrentCulture) ?? "-";
        MaterialsOrPartsNotes = Display(workOrder.MaterialsOrPartsNotes);
        ConditionAfter = string.IsNullOrWhiteSpace(workOrder.ConditionAfter) ? "-" : localization.LocalizeCondition(workOrder.ConditionAfter);
        OperationalStatusAfter = Display(workOrder.OperationalStatusAfter);
        CompletionNotes = Display(workOrder.CompletionNotes);
        CreatedBy = Display(workOrder.CreatedBy);
        CreatedAt = FormatDateTime(workOrder.CreatedAtUtc, localization);
        LastModifiedBy = Display(workOrder.LastModifiedBy);
        LastModifiedAt = FormatDateTime(workOrder.LastModifiedAtUtc, localization);
        Revision = workOrder.Revision.ToString(localization.CurrentCulture);
    }

    public WorkOrder WorkOrder { get; }
    public IReadOnlyList<ActivityDisplayRow> Activities { get; }
    public MaintenanceRecord? GeneratedMaintenanceRecord { get; }
    public bool CanEdit => WorkOrder.Status is not WorkOrderStatus.Completed and not WorkOrderStatus.Cancelled;
    public bool HasGeneratedMaintenanceRecord => GeneratedMaintenanceRecord is not null;
    public string GeneratedMaintenanceNumber => GeneratedMaintenanceRecord?.MaintenanceNumber ?? string.Empty;

    public string WorkOrderNumber { get; }
    public string Title { get; }
    public string AssetName { get; }
    public string AssetNumber { get; }
    public string Site { get; }
    public string Location { get; }
    public string MaintenanceTypeText { get; }
    public string Category { get; }
    public string Priority { get; }
    public string Status { get; }
    public string Problem { get; }
    public string RequestedBy { get; }
    public string ReportedAt { get; }
    public string DueDate { get; }
    public string AssignedTo { get; }
    public string StartedAt { get; }
    public string CompletedAt { get; }
    public string WorkPerformed { get; }
    public string PerformedBy { get; }
    public string Cost { get; }
    public string DowntimeHours { get; }
    public string MaterialsOrPartsNotes { get; }
    public string ConditionAfter { get; }
    public string OperationalStatusAfter { get; }
    public string CompletionNotes { get; }
    public string CreatedBy { get; }
    public string CreatedAt { get; }
    public string LastModifiedBy { get; }
    public string LastModifiedAt { get; }
    public string Revision { get; }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string FormatDateTime(DateTimeOffset? value, ILocalizationService localization) =>
        value.HasValue && value.Value != default ? value.Value.ToLocalTime().ToString("g", localization.CurrentCulture) : "-";

    private static string LocalizeMaintenanceType(MaintenanceType type, ILocalizationService localization) => type switch
    {
        MaintenanceType.CorrectiveMaintenance => localization.GetString("MaintenanceTypeCorrective"),
        MaintenanceType.PreventiveMaintenance => localization.GetString("MaintenanceTypePreventive"),
        MaintenanceType.Inspection => localization.GetString("MaintenanceTypeInspection"),
        MaintenanceType.RoutineService => localization.GetString("MaintenanceTypeRoutineService"),
        MaintenanceType.Breakdown => localization.GetString("MaintenanceTypeBreakdown"),
        MaintenanceType.Installation => localization.GetString("MaintenanceTypeInstallation"),
        MaintenanceType.Relocation => localization.GetString("MaintenanceTypeRelocation"),
        MaintenanceType.Replacement => localization.GetString("MaintenanceTypeReplacement"),
        MaintenanceType.TestOrCalibration => localization.GetString("MaintenanceTypeCalibration"),
        _ => localization.GetString("MaintenanceTypeGeneral")
    };


}
