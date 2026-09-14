using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Entities;

public sealed class MaintenancePlan
{
    public Guid MaintenancePlanId { get; set; } = Guid.NewGuid();
    public string MaintenancePlanNumber { get; set; } = string.Empty;
    public Guid? AssetId { get; set; }
    public string AssetNumber { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string MaintenanceCategory { get; set; } = string.Empty;
    public MaintenanceType MaintenanceType { get; set; } = MaintenanceType.PreventiveMaintenance;
    public string Instructions { get; set; } = string.Empty;
    public int FrequencyValue { get; set; } = 1;
    public MaintenanceFrequencyUnit FrequencyUnit { get; set; } = MaintenanceFrequencyUnit.Months;
    public DateOnly? LastCompletedDate { get; set; }
    public DateOnly NextDueDate { get; set; }
    public string AssignedTo { get; set; } = string.Empty;
    public decimal? EstimatedCost { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Revision { get; set; } = 1;
}
