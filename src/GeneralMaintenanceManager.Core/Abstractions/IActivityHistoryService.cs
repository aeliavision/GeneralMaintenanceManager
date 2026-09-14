using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IActivityHistoryService
{
    public Task<ActivityHistoryPage> GetPageAsync(ActivityHistoryQuery query, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<ActivityHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<ActivityHistoryEntry>> GetForYearAsync(int year, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<ActivityHistoryEntry>> GetForRecordAsync(string recordType, Guid recordId, CancellationToken cancellationToken = default);
    public Task<ActivityHistoryEntry> AppendAsync(ActivityHistoryDraft draft, CancellationToken cancellationToken = default);
}
