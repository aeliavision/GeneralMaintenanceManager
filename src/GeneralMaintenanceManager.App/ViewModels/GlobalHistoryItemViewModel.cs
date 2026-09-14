using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class GlobalHistoryItemViewModel
{
    public GlobalHistoryItemViewModel(MaintenanceRecord record, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localization);
        SourceMaintenanceRecord = record;
        RecordGroup = "Maintenance";
        RecordGroupText = localization.GetString("HistoryModeMaintenance");
        AssetNumber = record.AssetNumberSnapshot.Trim();
        AssetName = string.IsNullOrWhiteSpace(record.AssetNameSnapshot) ? record.Subject.Trim() : record.AssetNameSnapshot.Trim();
        Site = record.Site.Trim();
        Location = record.Location.Trim();
        LocationDisplay = BuildLocationDisplay(Site, Location);
        PerformedBy = record.PerformedBy.Trim();
        CostValue = record.Cost;
        CostText = FormatCost(CostValue, localization);
        RecordDate = DateOnly.FromDateTime(record.OccurredAt.LocalDateTime);
        RecordCreatedAtUtc = record.CreatedAtUtc;
        RecordKey = $"maintenance:{record.MaintenanceRecordId:D}";
        DateText = RecordDate.ToString("dd MMM yyyy", localization.CurrentCulture);
        TypeText = record.IsInvalid
            ? localization.GetString("MaintenanceMarkedInvalid")
            : MaintenanceTextPresentation.LocalizeMaintenanceType(record.MaintenanceType, localization);
        ReferenceNumber = record.MaintenanceNumber.Trim();
        Title = record.Subject.Trim();
        Description = record.WorkPerformed.Trim();
        Notes = record.Notes.Trim();
        HasDescription = Description.Length > 0;
        HasNotes = Notes.Length > 0;
    }

    public GlobalHistoryItemViewModel(ActivityHistoryEntry activity, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(localization);
        SourceActivity = activity;
        RecordGroup = activity.RecordType switch
        {
            ActivityRecordTypes.WorkOrder => "WorkOrders",
            ActivityRecordTypes.PreventiveMaintenance => "PreventiveMaintenance",
            _ => "Maintenance"
        };
        RecordGroupText = RecordGroup switch
        {
            "WorkOrders" => localization.GetString("HistoryModeWorkOrders"),
            "PreventiveMaintenance" => localization.GetString("HistoryModePreventive"),
            _ => localization.GetString("HistoryModeMaintenance")
        };
        AssetNumber = string.Empty;
        AssetName = activity.Subject.Trim();
        Site = activity.Site.Trim();
        Location = activity.Location.Trim();
        LocationDisplay = BuildLocationDisplay(Site, Location);
        PerformedBy = activity.ChangedBy.Trim();
        CostText = string.Empty;
        RecordDate = DateOnly.FromDateTime(activity.OccurredAtUtc.LocalDateTime);
        RecordCreatedAtUtc = activity.OccurredAtUtc;
        RecordKey = $"activity:{activity.ActivityHistoryEntryId:D}";
        DateText = RecordDate.ToString("dd MMM yyyy", localization.CurrentCulture);
        TypeText = MaintenanceTextPresentation.LocalizeActivityType(activity.ActivityType, localization);
        ReferenceNumber = activity.Reference.Trim();
        Title = activity.Subject.Trim();
        Description = MaintenanceTextPresentation.LocalizeActivityType(activity.ActivityType, localization);
        Notes = activity.Changes.Trim();
        HasDescription = Description.Length > 0;
        HasNotes = Notes.Length > 0;
    }

    public GlobalHistoryItemViewModel(MaintenancePlan plan, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(localization);
        SourceMaintenancePlan = plan;
        RecordGroup = "PreventiveMaintenance";
        RecordGroupText = localization.GetString("HistoryModePreventive");
        AssetNumber = plan.AssetNumber?.Trim() ?? string.Empty;
        AssetName = plan.PlanName.Trim();
        Site = plan.Site.Trim();
        Location = plan.Location.Trim();
        LocationDisplay = BuildLocationDisplay(Site, Location);
        PerformedBy = plan.AssignedTo.Trim();
        CostValue = plan.EstimatedCost;
        CostText = FormatCost(CostValue, localization);
        var date = plan.NextDueDate;
        RecordDate = date;
        RecordCreatedAtUtc = plan.UpdatedAtUtc;
        RecordKey = $"plan:{plan.MaintenancePlanId:D}";
        DateText = date.ToString("dd MMM yyyy", localization.CurrentCulture);
        TypeText = plan.IsActive ? localization.GetString("HistoryPreventivePlan") : localization.GetString("HistoryPreventivePaused");
        ReferenceNumber = plan.MaintenancePlanNumber.Length > 0 ? plan.MaintenancePlanNumber : $"PM-{plan.MaintenancePlanId.ToString("N")[..8].ToUpperInvariant()}";
        Title = plan.PlanName.Trim();
        Description = plan.Instructions.Trim();
        Notes = string.Empty;
        HasDescription = Description.Length > 0;
    }

    public GlobalHistoryItemViewModel(WorkOrder workOrder, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        ArgumentNullException.ThrowIfNull(localization);
        SourceWorkOrder = workOrder;
        RecordGroup = "WorkOrders";
        RecordGroupText = localization.GetString("HistoryModeWorkOrders");
        AssetName = workOrder.Title.Trim();
        AssetNumber = workOrder.AssetNumber?.Trim() ?? string.Empty;
        Site = workOrder.Site?.Trim() ?? string.Empty;
        Location = workOrder.Location?.Trim() ?? string.Empty;
        LocationDisplay = BuildLocationDisplay(Site, Location);
        PerformedBy = string.IsNullOrWhiteSpace(workOrder.PerformedBy) ? workOrder.AssignedTo.Trim() : workOrder.PerformedBy.Trim();
        CostValue = workOrder.Cost;
        CostText = FormatCost(CostValue, localization);
        var timestamp = workOrder.CompletedAtUtc ?? workOrder.LastModifiedAtUtc ?? workOrder.CreatedAtUtc;
        RecordDate = DateOnly.FromDateTime(timestamp.LocalDateTime);
        RecordCreatedAtUtc = timestamp;
        RecordKey = $"workorder:{workOrder.WorkOrderId:D}";
        DateText = RecordDate.ToString("dd MMM yyyy", localization.CurrentCulture);
        TypeText = MaintenanceTextPresentation.LocalizeWorkOrderStatus(workOrder.Status, localization);
        ReferenceNumber = workOrder.WorkOrderNumber?.Trim() ?? string.Empty;
        Title = workOrder.Title.Trim();
        Description = workOrder.Status == WorkOrderStatus.Completed && !string.IsNullOrWhiteSpace(workOrder.WorkPerformed)
            ? workOrder.WorkPerformed.Trim()
            : workOrder.ProblemDescription?.Trim() ?? string.Empty;
        Notes = workOrder.CompletionNotes?.Trim() ?? string.Empty;
        HasDescription = Description.Length > 0;
        HasNotes = Notes.Length > 0;
    }

    public Asset? Asset { get; }
    public MaintenanceRecord? SourceMaintenanceRecord { get; }
    public ActivityHistoryEntry? SourceActivity { get; }
    public WorkOrder? SourceWorkOrder { get; }
    public MaintenancePlan? SourceMaintenancePlan { get; }
    public string RecordGroup { get; }
    public string RecordGroupText { get; }
    public Guid? AssetId => Asset?.AssetId ?? SourceMaintenanceRecord?.AssetId ?? SourceWorkOrder?.AssetId ?? SourceMaintenancePlan?.AssetId;
    public bool HasAsset => AssetId.HasValue;
    public string RecordKey { get; }
    public DateOnly RecordDate { get; }
    public int RecordYear => RecordDate.Year;
    public DateTimeOffset RecordCreatedAtUtc { get; }
    public decimal? CostValue { get; }
    public string AssetNumber { get; }
    public string AssetName { get; }
    public string Site { get; }
    public string Location { get; }
    public string LocationDisplay { get; }
    public string DateText { get; }
    public string TypeText { get; }
    public string ReferenceNumber { get; }
    public string Title { get; }
    public string Description { get; }
    public string Notes { get; }
    public string PerformedBy { get; }
    public string CostText { get; }
    public bool HasDescription { get; }
    public bool HasNotes { get; }

    private static string BuildLocationDisplay(string site, string location) =>
        string.Join(" / ", new[] { site, location }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatCost(decimal? cost, ILocalizationService localization) =>
        cost.HasValue ? cost.Value.ToString("N2", localization.CurrentCulture) : string.Empty;


}