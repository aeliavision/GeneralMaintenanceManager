namespace GeneralMaintenanceManager.Core.Entities;

/// <summary>
/// Append-oriented audit/activity entry for meaningful maintenance operations.
/// Entries are never updated or deleted by normal application workflows.
/// </summary>
public sealed class ActivityHistoryEntry
{
    public Guid ActivityHistoryEntryId { get; set; } = Guid.NewGuid();
    public DateTimeOffset OccurredAtUtc { get; set; }
    public long OccurredAtUtcTicks { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Changes { get; set; } = string.Empty;
}

public static class ActivityRecordTypes
{
    public const string Maintenance = "Maintenance";
    public const string WorkOrder = "WorkOrder";
    public const string PreventiveMaintenance = "PreventiveMaintenance";
    public const string Settings = "Settings";
}

public static class ActivityTypes
{
    public const string MaintenanceCreated = "MaintenanceCreated";
    public const string MaintenanceEdited = "MaintenanceEdited";
    public const string MaintenanceMarkedInvalid = "MaintenanceMarkedInvalid";
    public const string WorkOrderCreated = "WorkOrderCreated";
    public const string WorkOrderAssigned = "WorkOrderAssigned";
    public const string WorkOrderStarted = "WorkOrderStarted";
    public const string WorkOrderResumed = "WorkOrderResumed";
    public const string WorkOrderPutOnHold = "WorkOrderPutOnHold";
    public const string WorkOrderCompleted = "WorkOrderCompleted";
    public const string WorkOrderCompletionRecovered = "WorkOrderCompletionRecovered";
    public const string WorkOrderCancelled = "WorkOrderCancelled";
    public const string WorkOrderEdited = "WorkOrderEdited";
    public const string PreventivePlanCreated = "PreventivePlanCreated";
    public const string PreventivePlanEdited = "PreventivePlanEdited";
    public const string PreventivePlanActivated = "PreventivePlanActivated";
    public const string PreventivePlanPaused = "PreventivePlanPaused";
    public const string PreventivePlanDue = "PreventivePlanDue";
    public const string PreventivePlanGeneratedWorkOrder = "PreventivePlanGeneratedWorkOrder";
    public const string PreventivePlanCompletedOccurrence = "PreventivePlanCompletedOccurrence";
}
