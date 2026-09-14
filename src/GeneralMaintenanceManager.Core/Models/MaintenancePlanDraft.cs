using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

public sealed record MaintenancePlanDraft(
    Guid? AssetId,
    string Site,
    string Location,
    string PlanName,
    string MaintenanceCategory,
    MaintenanceType MaintenanceType,
    string Instructions,
    int FrequencyValue,
    MaintenanceFrequencyUnit FrequencyUnit,
    DateOnly NextDueDate,
    string AssignedTo,
    decimal? EstimatedCost,
    bool IsActive);
