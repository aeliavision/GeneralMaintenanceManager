using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

public sealed record WorkOrderDraft(
    Guid? AssetId,
    Guid? MaintenancePlanId,
    string Site,
    string Location,
    string Title,
    string MaintenanceCategory,
    MaintenanceType MaintenanceType,
    WorkOrderPriority Priority,
    string ProblemDescription,
    string RequestedBy,
    DateOnly? DueDate,
    string AssignedTo,
    string Notes = "");
