using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IAssetService
{
    public Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken cancellationToken = default);
    public Task<Asset?> GetByIdAsync(Guid assetId, CancellationToken cancellationToken = default);
    public Task<Asset> AddAsync(AssetDraft draft, CancellationToken cancellationToken = default);
    public Task UpdateAsync(Guid assetId, AssetDraft draft, string reason = "Asset information corrected.", CancellationToken cancellationToken = default);
    public Task AssignAssetNumberAsync(Guid assetId, string assetNumber, string reason, CancellationToken cancellationToken = default);
    public Task ArchiveAsync(Guid assetId, string reason, CancellationToken cancellationToken = default);
}
