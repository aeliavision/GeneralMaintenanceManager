using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IDatabaseMaintenanceService
{
    Task<DatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task OptimizeAsync(CancellationToken cancellationToken = default);
    Task AnalyzeAndOptimizeAsync(CancellationToken cancellationToken = default);
    Task RebuildWorkOrderSearchAsync(CancellationToken cancellationToken = default);
    Task RebuildActivityHistorySearchAsync(CancellationToken cancellationToken = default);
    Task FullIntegrityCheckAsync(CancellationToken cancellationToken = default);
}
