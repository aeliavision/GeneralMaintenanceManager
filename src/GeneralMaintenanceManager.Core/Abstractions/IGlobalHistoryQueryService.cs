using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IGlobalHistoryQueryService
{
    Task<GlobalHistoryPage> GetPageAsync(GlobalHistoryQuery query, CancellationToken cancellationToken = default);
}
