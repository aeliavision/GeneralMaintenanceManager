using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IMaintenanceRecordService
{
    public Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default);
    public Task<MaintenancePage> GetPageAsync(MaintenanceQuery query, CancellationToken cancellationToken = default);
    public Task<MaintenanceActivityPage> GetActivityPageAsync(MaintenanceActivityQuery query, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<MaintenanceRecord>> GetAllMatchingAsync(MaintenanceQuery query, CancellationToken cancellationToken = default);
    public Task<MaintenanceEntrySuggestionCatalog> GetEntrySuggestionCatalogAsync(CancellationToken cancellationToken = default);
    public Task<MaintenanceRecord?> GetByNumberAsync(string maintenanceNumber, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<MaintenanceActivity>> GetActivityAsync(string maintenanceNumber, CancellationToken cancellationToken = default);
    public Task<MaintenanceRecord> CreateAsync(MaintenanceRecordDraft draft, CancellationToken cancellationToken = default);
    public Task<MaintenanceRecord> EnsureWorkOrderMaintenanceAsync(WorkOrderGeneratedMaintenanceDraft draft, CancellationToken cancellationToken = default);
    public Task<MaintenanceAggregate> GetAggregateAsync(MaintenanceQuery query, CancellationToken cancellationToken = default);
    public Task<MaintenanceDashboardSummary> GetDashboardSummaryAsync(int year, CancellationToken cancellationToken = default);
    public Task<MaintenanceDashboardSummaryVerification> VerifyDashboardSummariesAsync(int year, CancellationToken cancellationToken = default);
    public Task RebuildDashboardSummariesAsync(int year, CancellationToken cancellationToken = default);
    public Task<MaintenanceRecord> UpdateAsync(Guid maintenanceRecordId, int referenceYear, MaintenanceRecordEditDraft draft, CancellationToken cancellationToken = default);
    public Task<MaintenanceRecord> MarkInvalidAsync(Guid maintenanceRecordId, int referenceYear, string reason, CancellationToken cancellationToken = default);
}
