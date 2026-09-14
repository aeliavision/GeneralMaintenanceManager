using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

/// <summary>
/// Asset-history compatibility surface backed exclusively by canonical annual Maintenance records.
/// Reads canonical annual Maintenance records; no duplicate mirror exists behind this interface.
/// </summary>
public interface IMaintenanceHistoryService
{
    public Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<MaintenanceRecord>> GetHistoryAsync(Guid assetId, int? year, CancellationToken cancellationToken = default);
    public Task<MaintenanceHistoryPrintSelection> GetHistoryForPrintAsync(Guid assetId, int maximumRecords, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<MaintenanceActivity>> GetAuditTrailAsync(string maintenanceNumber, CancellationToken cancellationToken = default);
}
