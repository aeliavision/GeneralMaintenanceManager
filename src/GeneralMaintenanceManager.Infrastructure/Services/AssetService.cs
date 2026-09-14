using System.Text.Json;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class AssetService(DatabaseContextFactory contextFactory) : IAssetService
{
    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.Asset.AsNoTracking().Where(item => !item.IsArchived)
            .OrderBy(item => item.AssetNumber == string.Empty).ThenBy(item => item.AssetNumber).ThenBy(item => item.AssetName)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Asset?> GetByIdAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.Asset.AsNoTracking().SingleOrDefaultAsync(item => item.AssetId == assetId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Asset> AddAsync(AssetDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        var assetNumber = Clean(draft.AssetNumber);
        await using var context = contextFactory.CreateMasterDbContext();
        if (assetNumber.Length > 0 && await context.Asset.AnyAsync(
                item => !item.IsArchived && EF.Functions.Collate(item.AssetNumber, "NOCASE") == assetNumber,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Asset #{assetNumber} already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
            AssetId = Guid.NewGuid(),
            AssetNumber = assetNumber,
            ImportedAssetNumber = assetNumber,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        ApplyDraft(asset, draft);
        context.Asset.Add(asset);
        context.AssetAudit.Add(new AssetAudit
        {
            AuditId = Guid.NewGuid(),
            AssetId = asset.AssetId,
            Action = "Created",
            Reason = "Asset created.",
            Changes = Snapshot(asset),
            ChangedBy = Environment.UserName,
            ChangedAtUtc = now
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && assetNumber.Length > 0)
        {
            throw new InvalidOperationException($"Asset number #{assetNumber} is already assigned to another asset.");
        }
        return asset;
    }

    public async Task UpdateAsync(Guid assetId, AssetDraft draft, string reason = "Asset information corrected.", CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        await using var context = contextFactory.CreateMasterDbContext();
        var asset = await context.Asset.SingleOrDefaultAsync(item => item.AssetId == assetId && !item.IsArchived, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Asset was not found.");

        var before = Snapshot(asset);
        var requestedAsset = Clean(draft.AssetNumber);
        if (!string.Equals(requestedAsset, asset.AssetNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Asset numbers must be assigned or corrected through the audited asset-number action.");
        }

        ApplyDraft(asset, draft);
        asset.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var after = Snapshot(asset);
        if (before == after)
        {
            return;
        }

        context.AssetAudit.Add(CreateAudit(assetId, "DataCorrected", reason, before, after));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignAssetNumberAsync(Guid assetId, string assetNumber, string reason, CancellationToken cancellationToken = default)
    {
        var normalized = Clean(assetNumber);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Asset number is required.", nameof(assetNumber));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        await using var context = contextFactory.CreateMasterDbContext();
        var asset = await context.Asset.SingleOrDefaultAsync(item => item.AssetId == assetId && !item.IsArchived, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Asset was not found.");
        if (await context.Asset.AnyAsync(
                item => !item.IsArchived
                    && item.AssetId != assetId
                    && EF.Functions.Collate(item.AssetNumber, "NOCASE") == normalized,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Asset number #{normalized} is already assigned to another asset.");
        }

        var old = asset.AssetNumber;
        if (string.Equals(old, normalized, StringComparison.Ordinal))
        {
            return;
        }
        asset.AssetNumber = normalized;
        asset.UpdatedAtUtc = DateTimeOffset.UtcNow;
        context.AssetAudit.Add(CreateAudit(assetId, old.Length == 0 ? "AssetNumberAssigned" : "AssetNumberCorrected", reason, old, normalized));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException($"Asset number #{normalized} is already assigned to another asset.");
        }
        // Historical Maintenance snapshots intentionally remain unchanged when the current Asset number changes.
    }

    public async Task ArchiveAsync(Guid assetId, string reason, CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset ID is required.", nameof(assetId));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A removal reason is required.", nameof(reason));
        }

        await using var context = contextFactory.CreateMasterDbContext();
        var asset = await context.Asset
            .SingleOrDefaultAsync(item => item.AssetId == assetId && !item.IsArchived, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Asset was not found.");

        var liveWorkOrder = await context.WorkOrders.AsNoTracking()
            .Where(item => item.AssetId == assetId && !item.IsClosed)
            .OrderBy(item => item.WorkOrderNumber)
            .Select(item => item.WorkOrderNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(liveWorkOrder))
            throw new InvalidOperationException($"Asset cannot be archived while live Work Order {liveWorkOrder} references it. Complete, cancel, or reassign that Work Order first.");

        var activePlan = await context.MaintenancePlans.AsNoTracking()
            .Where(item => item.AssetId == assetId && item.IsActive)
            .OrderBy(item => item.MaintenancePlanNumber)
            .Select(item => item.MaintenancePlanNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(activePlan))
            throw new InvalidOperationException($"Asset cannot be archived while active Preventive Maintenance plan {activePlan} references it. Pause or reassign that plan first.");

        var now = DateTimeOffset.UtcNow;
        asset.IsArchived = true;
        asset.UpdatedAtUtc = now;
        context.AssetAudit.Add(new AssetAudit
        {
            AuditId = Guid.NewGuid(),
            AssetId = asset.AssetId,
            Action = "AssetRemoved",
            Reason = reason.Trim(),
            Changes = JsonSerializer.Serialize(new
            {
                asset.AssetNumber,
                asset.AssetName,
                PreservedHistory = true
            }),
            ChangedBy = Environment.UserName,
            ChangedAtUtc = now
        });

        var pendingReviews = await context.ReviewItems
            .Where(item => item.Status == "Pending"
                && (item.AssetIdA == assetId || item.AssetIdB == assetId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var review in pendingReviews)
        {
            review.Status = "Resolved";
            review.Resolution = "AssetRemoved";
            review.ResolvedBy = Environment.UserName;
            review.ResolvedAtUtc = now;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AssetAudit CreateAudit(Guid assetId, string action, string reason, string before, string after) => new()
    {
        AuditId = Guid.NewGuid(), AssetId = assetId, Action = action, Reason = reason,
        Changes = JsonSerializer.Serialize(new { Before = before, After = after }),
        ChangedBy = Environment.UserName, ChangedAtUtc = DateTimeOffset.UtcNow
    };

    private static string Snapshot(Asset e) => JsonSerializer.Serialize(new
    {
        e.AssetNumber, e.Site, e.Location, e.AssetName, e.AssetCategory, e.Manufacturer, e.Country, e.Model,
        e.SerialNumber, e.Condition, e.OperationalStatus, e.TechnicalSpecification, e.PurchaseDate,
        e.PurchasePrice, e.InstallationDate, e.WarrantyExpiryDate, e.Notes
    });

    private static void ValidateDraft(AssetDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.AssetName))
        {
            throw new ArgumentException("Asset name is required.", nameof(draft));
        }
        ExactNumericStorage.ValidateMoney(draft.PurchasePrice, nameof(draft));
    }

    private static void ApplyDraft(Asset asset, AssetDraft draft)
    {
        asset.Site = Clean(draft.Site);
        asset.Location = Clean(draft.Location);
        asset.AssetName = Clean(draft.AssetName);
        asset.AssetCategory = Clean(draft.AssetCategory);
        asset.Manufacturer = Clean(draft.Manufacturer);
        asset.Country = Clean(draft.Country);
        asset.Model = Clean(draft.Model);
        asset.SerialNumber = Clean(draft.SerialNumber);
        asset.Condition = Clean(draft.Condition);
        asset.OperationalStatus = Clean(draft.OperationalStatus).Length == 0 ? "In Service" : Clean(draft.OperationalStatus);
        asset.TechnicalSpecification = Clean(draft.TechnicalSpecification);
        asset.PurchaseDate = draft.PurchaseDate;
        asset.PurchasePrice = draft.PurchasePrice;
        asset.InstallationDate = draft.InstallationDate;
        asset.WarrantyExpiryDate = draft.WarrantyExpiryDate;
        asset.Notes = Clean(draft.Notes);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        const int sqliteConstraintUnique = 2067;
        return exception.InnerException is SqliteException sqliteException
            && sqliteException.SqliteExtendedErrorCode == sqliteConstraintUnique;
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
