using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class ReviewService(
    DatabaseContextFactory contextFactory) : IReviewService
{
    private const string Pending = "Pending";

    public async Task<IReadOnlyList<ReviewCase>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        var reviews = await context.ReviewItems.AsNoTracking()
            .Where(item => item.Status == Pending)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        // EF Core's SQLite provider cannot translate ORDER BY for DateTimeOffset.
        // Keep the filtering in SQLite, then apply the deterministic presentation
        // ordering in memory after the rows have been materialized.
        reviews = reviews
            .OrderBy(item => item.ReviewType, StringComparer.Ordinal)
            .ThenBy(item => item.CreatedAtUtc)
            .ToArray();

        if (reviews.Length == 0)
        {
            return [];
        }

        var assetIds = reviews.SelectMany(item => item.AssetIdB.HasValue
                ? new[] { item.AssetIdA, item.AssetIdB.Value }
                : new[] { item.AssetIdA })
            .Distinct()
            .ToArray();
        var asset = await context.Asset.AsNoTracking()
            .Where(item => assetIds.Contains(item.AssetId))
            .ToDictionaryAsync(item => item.AssetId, cancellationToken).ConfigureAwait(false);

        var results = new List<ReviewCase>(reviews.Length);
        foreach (var review in reviews)
        {
            if (!asset.TryGetValue(review.AssetIdA, out var a) || a.IsArchived)
            {
                continue;
            }

            Asset? b = null;
            if (review.AssetIdB.HasValue)
            {
                if (!asset.TryGetValue(review.AssetIdB.Value, out b) || b.IsArchived)
                {
                    continue;
                }
            }

            results.Add(new ReviewCase(review.ReviewId, review.ReviewType, review.Reasons, a, b, review.Status));
        }

        return results;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        // Keep broad duplicate detection inside SQLite. Only assets capable of producing
        // a review case are materialized: missing-number rows plus their conflicting
        // owners, exact serial duplicate groups, and strong no-serial signature groups.
        var asset = await context.Asset.FromSqlRaw("""
            SELECT a.*
            FROM Asset AS a
            WHERE a.IsArchived = 0
              AND (
                    TRIM(a.AssetNumber) = ''
                 OR (TRIM(a.AssetNumber) <> '' AND EXISTS (
                        SELECT 1 FROM Asset AS m
                        WHERE m.IsArchived = 0 AND m.AssetId <> a.AssetId
                          AND TRIM(m.AssetNumber) = ''
                          AND TRIM(m.ImportedAssetNumber) <> ''
                          AND UPPER(TRIM(m.ImportedAssetNumber)) = UPPER(TRIM(a.AssetNumber))))
                 OR (TRIM(a.SerialNumber) <> '' AND EXISTS (
                        SELECT 1 FROM Asset AS s
                        WHERE s.IsArchived = 0 AND s.AssetId <> a.AssetId
                          AND UPPER(TRIM(s.SerialNumber)) = UPPER(TRIM(a.SerialNumber))))
                 OR (TRIM(a.SerialNumber) = ''
                     AND TRIM(a.AssetName) <> ''
                     AND (TRIM(a.Manufacturer) <> '' OR TRIM(a.Model) <> '')
                     AND (TRIM(a.Site) <> '' OR TRIM(a.Location) <> '')
                     AND EXISTS (
                        SELECT 1 FROM Asset AS d
                        WHERE d.IsArchived = 0 AND d.AssetId <> a.AssetId
                          AND TRIM(d.SerialNumber) = ''
                          AND UPPER(TRIM(d.AssetName)) = UPPER(TRIM(a.AssetName))
                          AND UPPER(TRIM(d.Manufacturer)) = UPPER(TRIM(a.Manufacturer))
                          AND UPPER(TRIM(d.Model)) = UPPER(TRIM(a.Model))
                          AND UPPER(TRIM(d.Site)) = UPPER(TRIM(a.Site))
                          AND UPPER(TRIM(d.Location)) = UPPER(TRIM(a.Location))))
                  )
            """).AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var reviews = await context.ReviewItems.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var reviewIndex = BuildReviewIndex(reviews);
        var touched = new HashSet<Guid>();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in asset.Where(item => string.IsNullOrWhiteSpace(item.AssetNumber)))
        {
            EnsureReview(context, reviewIndex, touched, "MissingAsset", item.AssetId, null,
                "MissingAsset", now);
        }

        var ownersByAsset = asset.Where(item => !string.IsNullOrWhiteSpace(item.AssetNumber))
            .GroupBy(item => Normalize(item.AssetNumber))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var item in asset.Where(item => string.IsNullOrWhiteSpace(item.AssetNumber) && !string.IsNullOrWhiteSpace(item.ImportedAssetNumber)))
        {
            if (ownersByAsset.TryGetValue(Normalize(item.ImportedAssetNumber), out var owner) && owner.AssetId != item.AssetId)
            {
                EnsureReview(context, reviewIndex, touched, "AssetConflict", item.AssetId, owner.AssetId,
                    $"AssetConflict={item.ImportedAssetNumber}", now);
            }
        }

        // Exact serial number is the strongest duplicate signal.
        foreach (var group in asset.Where(item => !string.IsNullOrWhiteSpace(item.SerialNumber))
                     .GroupBy(item => Normalize(item.SerialNumber))
                     .Where(group => group.Count() > 1))
        {
            var items = group.OrderBy(item => item.AssetId).ToArray();
            var canonical = items[0];
            foreach (var candidate in items.Skip(1))
            {
                var reasons = BuildDuplicateReasons(canonical, candidate, exactSerial: true);
                EnsureReview(context, reviewIndex, touched, "PossibleDuplicate", canonical.AssetId, candidate.AssetId, reasons, now);
            }
        }

        // For records without a reliable serial, require a strong multi-field signature.
        var signatureGroups = asset
            .Where(item => string.IsNullOrWhiteSpace(item.SerialNumber))
            .Where(item => !string.IsNullOrWhiteSpace(item.AssetName))
            .GroupBy(BuildStrongSignature)
            .Where(group => group.Key.Length > 0 && group.Count() > 1);
        foreach (var group in signatureGroups)
        {
            var items = group.OrderBy(item => item.AssetId).ToArray();
            var canonical = items[0];
            foreach (var candidate in items.Skip(1))
            {
                EnsureReview(context, reviewIndex, touched, "PossibleDuplicate", canonical.AssetId, candidate.AssetId,
                    BuildDuplicateReasons(canonical, candidate, exactSerial: false), now);
            }
        }

        foreach (var review in reviews.Where(item => item.Status == Pending && !touched.Contains(item.ReviewId)))
        {
            review.Status = "Resolved";
            review.Resolution = "DataCorrected";
            review.ResolvedBy = Environment.UserName;
            review.ResolvedAtUtc = now;
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task KeepBothAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        var review = await context.ReviewItems.SingleAsync(item => item.ReviewId == reviewId, cancellationToken).ConfigureAwait(false);
        Resolve(review, "KeepBoth");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ResolveLaterAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        // Review Later is intentionally non-destructive: the review row remains Pending.
        // The WPF presentation hides it for the current visit and shows it again when
        // the user next enters Review. No database state should mark it as resolved.
        _ = reviewId;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task MergeAsync(Guid reviewId, Guid survivorAssetId, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        var review = await context.ReviewItems.SingleAsync(item => item.ReviewId == reviewId, cancellationToken).ConfigureAwait(false);
        if (!review.AssetIdB.HasValue)
        {
            throw new InvalidOperationException("This review item does not contain two asset records.");
        }
        if (survivorAssetId != review.AssetIdA && survivorAssetId != review.AssetIdB.Value)
        {
            throw new InvalidOperationException("The selected survivor is not part of this review item.");
        }

        var loserId = survivorAssetId == review.AssetIdA ? review.AssetIdB.Value : review.AssetIdA;
        var survivor = await context.Asset.SingleAsync(item => item.AssetId == survivorAssetId && !item.IsArchived, cancellationToken).ConfigureAwait(false);
        var loser = await context.Asset.SingleAsync(item => item.AssetId == loserId && !item.IsArchived, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var loserAsset = loser.AssetNumber;
        loser.AssetNumber = string.Empty;
        loser.IsArchived = true;
        loser.MergedIntoAssetId = survivor.AssetId;
        loser.UpdatedAtUtc = now;

        // Persist the loser first so the partial unique AssetNumber index is released
        // before a previously-unassigned survivor inherits that number.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(survivor.AssetNumber) && !string.IsNullOrWhiteSpace(loserAsset))
        {
            survivor.AssetNumber = loserAsset;
        }
        survivor.UpdatedAtUtc = now;

        var dependentWorkOrders = await context.WorkOrders
            .Where(item => item.AssetId == loser.AssetId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var order in dependentWorkOrders)
        {
            order.AssetId = survivor.AssetId;
            order.AssetNumber = survivor.AssetNumber;
            order.Revision = Math.Max(order.Revision, 1) + 1;
            order.LastModifiedAtUtc = now;
            order.LastModifiedBy = Environment.UserName;
            WorkOrderService.UpdateQueryProjection(order, now);
            context.WorkOrderAudit.Add(new WorkOrderAudit
            {
                AuditId = Guid.NewGuid(),
                WorkOrderId = order.WorkOrderId,
                Action = "AssetMerged",
                Changes = $"AssetId={loser.AssetId:D} -> {survivor.AssetId:D}; AssetNumber={survivor.AssetNumber}",
                ChangedBy = Environment.UserName,
                ChangedAtUtc = now
            });
        }

        var dependentPlans = await context.MaintenancePlans
            .Where(item => item.AssetId == loser.AssetId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var plan in dependentPlans)
        {
            plan.AssetId = survivor.AssetId;
            plan.AssetNumber = survivor.AssetNumber;
            plan.Revision = Math.Max(plan.Revision, 1) + 1;
            plan.UpdatedAtUtc = now;
            context.MaintenancePlanAudit.Add(new MaintenancePlanAudit
            {
                AuditId = Guid.NewGuid(),
                MaintenancePlanId = plan.MaintenancePlanId,
                Action = "AssetMerged",
                Changes = $"AssetId={loser.AssetId:D} -> {survivor.AssetId:D}; AssetNumber={survivor.AssetNumber}",
                ChangedBy = Environment.UserName,
                ChangedAtUtc = now
            });
        }

        var pendingIntents = await context.WorkOrderCompletionIntents
            .Where(item => item.AssetId == loser.AssetId && item.State != WorkOrderCompletionIntentStates.Finalized)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var intent in pendingIntents)
        {
            intent.AssetId = survivor.AssetId;
            intent.AssetNumberSnapshot = survivor.AssetNumber;
            intent.AssetNameSnapshot = survivor.AssetName;
            intent.UpdatedAtUtc = now;
        }

        context.AssetAudit.Add(new AssetAudit
        {
            AuditId = Guid.NewGuid(), AssetId = survivor.AssetId, Action = "MergeReceived",
            Reason = "Confirmed duplicate during review.", Changes = $"Merged asset {loser.AssetId} into this record.",
            ChangedBy = Environment.UserName, ChangedAtUtc = now
        });
        context.AssetAudit.Add(new AssetAudit
        {
            AuditId = Guid.NewGuid(), AssetId = loser.AssetId, Action = "MergedArchived",
            Reason = "Confirmed duplicate during review.", Changes = $"Merged into asset {survivor.AssetId}.",
            ChangedBy = Environment.UserName, ChangedAtUtc = now
        });

        foreach (var pending in await context.ReviewItems.Where(item => item.Status == Pending &&
                     (item.AssetIdA == loser.AssetId || item.AssetIdB == loser.AssetId)).ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            Resolve(pending, "Merged");
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Canonical annual Maintenance snapshots intentionally remain historical.
        // A master Asset merge must not rewrite their original AssetId/number/name snapshots.
    }

    private static void EnsureReview(
        MasterDbContext context,
        Dictionary<ReviewKey, ReviewItem> existing,
        HashSet<Guid> touched,
        string type,
        Guid a,
        Guid? b,
        string reasons,
        DateTimeOffset now)
    {
        Canonicalize(ref a, ref b);
        var key = new ReviewKey(type, a, b);
        if (existing.TryGetValue(key, out var match))
        {
            if (match.Status == "Resolved" && match.Resolution == "KeepBoth")
            {
                touched.Add(match.ReviewId);
                return;
            }
            if (match.Status != Pending)
            {
                match.Status = Pending;
                match.Resolution = string.Empty;
                match.ResolvedAtUtc = null;
                match.ResolvedBy = string.Empty;
            }
            match.Reasons = reasons;
            touched.Add(match.ReviewId);
            return;
        }

        var review = new ReviewItem
        {
            ReviewId = Guid.NewGuid(), ReviewType = type, AssetIdA = a, AssetIdB = b,
            Reasons = reasons, Status = Pending, CreatedAtUtc = now
        };
        context.ReviewItems.Add(review);
        existing.Add(key, review);
        touched.Add(review.ReviewId);
    }

    private static Dictionary<ReviewKey, ReviewItem> BuildReviewIndex(IEnumerable<ReviewItem> reviews)
    {
        var index = new Dictionary<ReviewKey, ReviewItem>();
        foreach (var review in reviews)
        {
            var a = review.AssetIdA;
            var b = review.AssetIdB;
            Canonicalize(ref a, ref b);
            var key = new ReviewKey(review.ReviewType, a, b);

            if (!index.TryGetValue(key, out var current) || ShouldPreferReview(review, current))
            {
                index[key] = review;
            }
        }

        return index;
    }

    private static bool ShouldPreferReview(ReviewItem candidate, ReviewItem current)
    {
        var candidateKeepBoth = candidate.Status == "Resolved" && candidate.Resolution == "KeepBoth";
        var currentKeepBoth = current.Status == "Resolved" && current.Resolution == "KeepBoth";
        if (candidateKeepBoth != currentKeepBoth)
        {
            return candidateKeepBoth;
        }

        return candidate.CreatedAtUtc > current.CreatedAtUtc;
    }

    private static void Resolve(ReviewItem review, string resolution)
    {
        review.Status = "Resolved";
        review.Resolution = resolution;
        review.ResolvedBy = Environment.UserName;
        review.ResolvedAtUtc = DateTimeOffset.UtcNow;
    }

    private readonly record struct ReviewKey(string ReviewType, Guid AssetIdA, Guid? AssetIdB);

    private static void Canonicalize(ref Guid a, ref Guid? b)
    {
        if (!b.HasValue || a.CompareTo(b.Value) <= 0)
        {
            return;
        }
        var temp = a;
        a = b.Value;
        b = temp;
    }

    private static string BuildStrongSignature(Asset item)
    {
        var assetName = Normalize(item.AssetName);
        var company = Normalize(item.Manufacturer);
        var model = Normalize(item.Model);
        var hospital = Normalize(item.Site);
        var department = Normalize(item.Location);
        if (assetName.Length == 0
            || (company.Length == 0 && model.Length == 0)
            || (hospital.Length == 0 && department.Length == 0))
        {
            return string.Empty;
        }
        return string.Join('|', assetName, company, model, hospital, department);
    }

    private static string BuildDuplicateReasons(Asset a, Asset b, bool exactSerial)
    {
        var reasons = new List<string>();
        if (exactSerial)
        {
            reasons.Add($"ExactSerial={a.SerialNumber.Trim()}");
        }
        AddReason(reasons, "SameMachine", a.AssetName, b.AssetName);
        AddReason(reasons, "SameManufacturer", a.Manufacturer, b.Manufacturer);
        AddReason(reasons, "SameModel", a.Model, b.Model);
        if (Same(a.Site, b.Site) && Same(a.Location, b.Location) && !string.IsNullOrWhiteSpace(a.Site))
        {
            reasons.Add($"SameLocation={a.Site.Trim()} / {a.Location.Trim()}");
        }
        return string.Join(Environment.NewLine, reasons);
    }

    private static void AddReason(List<string> reasons, string label, string a, string b)
    {
        if (Same(a, b) && !string.IsNullOrWhiteSpace(a))
        {
            reasons.Add($"{label}={a.Trim()}");
        }
    }

    private static bool Same(string? a, string? b) => Normalize(a) == Normalize(b);
    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
