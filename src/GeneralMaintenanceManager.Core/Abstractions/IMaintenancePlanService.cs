using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IMaintenancePlanService
{
    public Task<IReadOnlyList<MaintenancePlan>> GetAllAsync(CancellationToken cancellationToken = default);
    public Task<int> GetDueCountAsync(DateOnly asOfDate, CancellationToken cancellationToken = default);
    public Task ProcessDueActivitiesAsync(DateOnly asOfDate, CancellationToken cancellationToken = default);
    public Task<MaintenancePlan> CreateAsync(MaintenancePlanDraft draft, CancellationToken cancellationToken = default);
    public Task UpdateAsync(Guid maintenancePlanId, MaintenancePlanDraft draft, CancellationToken cancellationToken = default);
    public Task SetActiveAsync(Guid maintenancePlanId, bool isActive, CancellationToken cancellationToken = default);
    public Task<WorkOrder> GenerateWorkOrderAsync(Guid maintenancePlanId, CancellationToken cancellationToken = default);
}
