using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IWorkOrderService
{
    public Task<WorkOrderPage> GetPageAsync(WorkOrderQuery query, CancellationToken cancellationToken = default);
    public Task<MaintenanceEntrySuggestionCatalog> GetEntrySuggestionCatalogAsync(CancellationToken cancellationToken = default);
    public void InvalidateSuggestionCache();
    public Task<WorkOrderDashboardMetrics> GetDashboardMetricsAsync(CancellationToken cancellationToken = default);
    public Task<WorkOrder?> GetByIdAsync(Guid workOrderId, CancellationToken cancellationToken = default);
    public Task<WorkOrder?> GetByNumberAsync(string workOrderNumber, CancellationToken cancellationToken = default);
    public Task<WorkOrder> CreateAsync(WorkOrderDraft draft, CancellationToken cancellationToken = default);
    public Task UpdateAsync(Guid workOrderId, WorkOrderDraft draft, CancellationToken cancellationToken = default);
    public Task AssignAsync(Guid workOrderId, string assignedTo, CancellationToken cancellationToken = default);
    public Task StartAsync(Guid workOrderId, CancellationToken cancellationToken = default);
    public Task ResumeAsync(Guid workOrderId, CancellationToken cancellationToken = default);
    public Task PutOnHoldAsync(Guid workOrderId, string reason, CancellationToken cancellationToken = default);
    public Task CompleteAsync(WorkOrderCompletionDraft draft, CancellationToken cancellationToken = default);
    public Task CancelAsync(Guid workOrderId, string reason, CancellationToken cancellationToken = default);
    public Task RecoverPendingCompletionsAsync(CancellationToken cancellationToken = default);
}
