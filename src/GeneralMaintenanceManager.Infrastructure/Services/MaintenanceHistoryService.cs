using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Infrastructure.Services;

/// <summary>
/// Asset-history adapter over the canonical annual Maintenance service.
/// </summary>
public sealed class MaintenanceHistoryService(IMaintenanceRecordService maintenanceRecords) : IMaintenanceHistoryService
{
    private const int MaximumPageSize = 200;

    public Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default) =>
        maintenanceRecords.GetAvailableYearsAsync(cancellationToken);

    public Task<IReadOnlyList<MaintenanceRecord>> GetHistoryAsync(Guid assetId, int? year, CancellationToken cancellationToken = default) =>
        assetId == Guid.Empty
            ? Task.FromResult<IReadOnlyList<MaintenanceRecord>>([])
            : maintenanceRecords.GetAllMatchingAsync(
                new MaintenanceQuery(Year: year, AssetId: assetId, IncludeInvalid: true),
                cancellationToken);

    public async Task<MaintenanceHistoryPrintSelection> GetHistoryForPrintAsync(
        Guid assetId,
        int maximumRecords,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty) return new MaintenanceHistoryPrintSelection([], false);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecords, 1);

        var requested = checked(maximumRecords + 1);
        var items = new List<MaintenanceRecord>(Math.Min(requested, MaximumPageSize));
        MaintenancePageCursor? cursor = null;

        while (items.Count < requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = requested - items.Count;
            var page = await maintenanceRecords.GetPageAsync(
                new MaintenanceQuery(
                    AssetId: assetId,
                    IncludeInvalid: true,
                    PageSize: Math.Min(MaximumPageSize, remaining),
                    Cursor: cursor),
                cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Items);
            if (items.Count >= requested || !page.HasMore || page.NextCursor is null) break;
            if (page.NextCursor == cursor)
                throw new InvalidOperationException("Maintenance paging did not advance while preparing print history.");
            cursor = page.NextCursor;
        }

        var truncated = items.Count > maximumRecords;
        if (truncated) items.RemoveRange(maximumRecords, items.Count - maximumRecords);
        return new MaintenanceHistoryPrintSelection(items, truncated);
    }

    public Task<IReadOnlyList<MaintenanceActivity>> GetAuditTrailAsync(string maintenanceNumber, CancellationToken cancellationToken = default) =>
        maintenanceRecords.GetActivityAsync(maintenanceNumber, cancellationToken);
}
