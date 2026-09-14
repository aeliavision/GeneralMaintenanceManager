using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IReviewService
{
    public Task<IReadOnlyList<ReviewCase>> GetPendingAsync(CancellationToken cancellationToken = default);
    public Task RefreshAsync(CancellationToken cancellationToken = default);
    public Task KeepBothAsync(Guid reviewId, CancellationToken cancellationToken = default);
    public Task MergeAsync(Guid reviewId, Guid survivorAssetId, CancellationToken cancellationToken = default);
    public Task ResolveLaterAsync(Guid reviewId, CancellationToken cancellationToken = default);
}
