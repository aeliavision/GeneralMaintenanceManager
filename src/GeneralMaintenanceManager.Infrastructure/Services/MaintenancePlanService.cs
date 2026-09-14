using System.Text.Json;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class MaintenancePlanService(DatabaseContextFactory contextFactory) : IMaintenancePlanService
{
    public async Task<IReadOnlyList<MaintenancePlan>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.MaintenancePlans.AsNoTracking()
            .OrderByDescending(item => item.IsActive)
            .ThenBy(item => item.NextDueDate)
            .ThenBy(item => item.PlanName)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MaintenancePlan> CreateAsync(MaintenancePlanDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        await using var context = contextFactory.CreateMasterDbContext();
        var asset = await ResolveAssetAsync(context, draft.AssetId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var plan = new MaintenancePlan
        {
            MaintenancePlanId = Guid.NewGuid(),
            MaintenancePlanNumber = await ReferenceNumberAllocator.ReservePreventiveAsync(context, now.Year, cancellationToken).ConfigureAwait(false),
            AssetId = asset?.AssetId,
            AssetNumber = asset?.AssetNumber ?? string.Empty,
            Site = Clean(draft.Site).Length > 0 ? Clean(draft.Site) : asset?.Site ?? string.Empty,
            Location = Clean(draft.Location).Length > 0 ? Clean(draft.Location) : asset?.Location ?? string.Empty,
            PlanName = Clean(draft.PlanName),
            MaintenanceCategory = Clean(draft.MaintenanceCategory),
            MaintenanceType = NormalizeType(draft.MaintenanceType),
            Instructions = Clean(draft.Instructions),
            FrequencyValue = draft.FrequencyValue,
            FrequencyUnit = draft.FrequencyUnit,
            NextDueDate = draft.NextDueDate,
            AssignedTo = Clean(draft.AssignedTo),
            EstimatedCost = draft.EstimatedCost,
            IsActive = draft.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 1
        };
        if (plan.Location.Length == 0) throw new ArgumentException("Location is required for preventive maintenance.", nameof(draft));
        context.MaintenancePlans.Add(plan);
        context.MaintenancePlanAudit.Add(CreateAudit(plan, "Created", Snapshot(plan)));
        context.ActivityHistory.Add(CreateActivity(plan, ActivityTypes.PreventivePlanCreated, "Preventive Maintenance plan created.", Snapshot(plan)));
        if (!plan.IsActive)
            context.ActivityHistory.Add(CreateActivity(plan, ActivityTypes.PreventivePlanPaused, "Preventive Maintenance plan created paused.", "Active=false"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async Task UpdateAsync(Guid maintenancePlanId, MaintenancePlanDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        await using var context = contextFactory.CreateMasterDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var plan = await context.MaintenancePlans.SingleOrDefaultAsync(item => item.MaintenancePlanId == maintenancePlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Preventive maintenance plan was not found.");
        var before = Snapshot(plan);
        var wasActive = plan.IsActive;
        var asset = await ResolveAssetAsync(context, draft.AssetId, cancellationToken).ConfigureAwait(false);
        plan.AssetId = asset?.AssetId;
        plan.AssetNumber = asset?.AssetNumber ?? string.Empty;
        plan.Site = Clean(draft.Site).Length > 0 ? Clean(draft.Site) : asset?.Site ?? string.Empty;
        plan.Location = Clean(draft.Location).Length > 0 ? Clean(draft.Location) : asset?.Location ?? string.Empty;
        plan.PlanName = Clean(draft.PlanName);
        plan.MaintenanceCategory = Clean(draft.MaintenanceCategory);
        plan.MaintenanceType = NormalizeType(draft.MaintenanceType);
        plan.Instructions = Clean(draft.Instructions);
        plan.FrequencyValue = draft.FrequencyValue;
        plan.FrequencyUnit = draft.FrequencyUnit;
        plan.NextDueDate = draft.NextDueDate;
        plan.AssignedTo = Clean(draft.AssignedTo);
        plan.EstimatedCost = draft.EstimatedCost;
        plan.IsActive = draft.IsActive;
        if (plan.Location.Length == 0) throw new ArgumentException("Location is required for preventive maintenance.", nameof(draft));
        var after = Snapshot(plan);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        plan.Revision = Math.Max(plan.Revision, 1) + 1;
        plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var changes = JsonSerializer.Serialize(new { Before = before, After = after });
        context.MaintenancePlanAudit.Add(CreateAudit(plan, "Updated", changes));
        context.ActivityHistory.Add(CreateActivity(plan, ActivityTypes.PreventivePlanEdited, "Preventive Maintenance plan updated.", changes));
        if (wasActive != plan.IsActive)
            context.ActivityHistory.Add(CreateActivity(plan, plan.IsActive ? ActivityTypes.PreventivePlanActivated : ActivityTypes.PreventivePlanPaused,
                plan.IsActive ? "Preventive Maintenance plan activated." : "Preventive Maintenance plan paused.", $"Active={plan.IsActive}"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetActiveAsync(Guid maintenancePlanId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var plan = await context.MaintenancePlans.SingleOrDefaultAsync(item => item.MaintenancePlanId == maintenancePlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Preventive maintenance plan was not found.");
        if (plan.IsActive == isActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (isActive && plan.AssetId.HasValue)
        {
            var assetExists = await context.Asset.AsNoTracking()
                .AnyAsync(item => item.AssetId == plan.AssetId.Value && !item.IsArchived, cancellationToken)
                .ConfigureAwait(false);
            if (!assetExists)
                throw new InvalidOperationException("This Preventive Maintenance plan cannot be activated because its Asset is archived or missing. Reassign the plan to an active Asset first.");
        }
        var before = plan.IsActive;
        plan.IsActive = isActive;
        plan.Revision = Math.Max(plan.Revision, 1) + 1;
        plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
        context.MaintenancePlanAudit.Add(CreateAudit(plan, isActive ? "Activated" : "Paused", $"Active: {before} -> {isActive}"));
        context.ActivityHistory.Add(CreateActivity(plan, isActive ? ActivityTypes.PreventivePlanActivated : ActivityTypes.PreventivePlanPaused,
            isActive ? "Preventive Maintenance plan activated." : "Preventive Maintenance plan paused.", $"Active={isActive}"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkOrder> GenerateWorkOrderAsync(Guid maintenancePlanId, CancellationToken cancellationToken = default)
    {
        if (maintenancePlanId == Guid.Empty) throw new ArgumentException("Preventive Maintenance plan ID is required.", nameof(maintenancePlanId));
        await using var context = contextFactory.CreateMasterDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var plan = await context.MaintenancePlans.SingleOrDefaultAsync(item => item.MaintenancePlanId == maintenancePlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Preventive maintenance plan was not found.");
        if (!plan.IsActive) throw new InvalidOperationException("The preventive maintenance plan is inactive.");
        if (plan.AssetId.HasValue)
        {
            var assetExists = await context.Asset.AsNoTracking()
                .AnyAsync(item => item.AssetId == plan.AssetId.Value && !item.IsArchived, cancellationToken)
                .ConfigureAwait(false);
            if (!assetExists)
                throw new InvalidOperationException("The Preventive Maintenance plan references an archived or missing Asset. Reassign the plan before generating a Work Order.");
        }

        var existing = await context.WorkOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MaintenancePlanId == maintenancePlanId
                && item.Status != WorkOrderStatus.Completed
                && item.Status != WorkOrderStatus.Cancelled, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var order = new WorkOrder
        {
            WorkOrderId = Guid.NewGuid(),
            WorkOrderNumber = await ReferenceNumberAllocator.ReserveWorkOrderAsync(context, now.Year, cancellationToken).ConfigureAwait(false),
            AssetId = plan.AssetId,
            AssetNumber = plan.AssetNumber,
            MaintenancePlanId = plan.MaintenancePlanId,
            Site = plan.Site,
            Location = plan.Location,
            Title = plan.PlanName,
            MaintenanceCategory = plan.MaintenanceCategory,
            MaintenanceType = plan.MaintenanceType,
            Priority = WorkOrderPriority.Normal,
            ProblemDescription = plan.Instructions.Length > 0 ? plan.Instructions : plan.PlanName,
            RequestedBy = Environment.UserName,
            ReportedAtUtc = now,
            DueDate = plan.NextDueDate,
            Status = plan.AssignedTo.Length == 0 ? WorkOrderStatus.New : WorkOrderStatus.Assigned,
            AssignedTo = plan.AssignedTo,
            CreatedBy = Environment.UserName,
            CreatedAtUtc = now,
            Revision = 1
        };
        WorkOrderService.UpdateQueryProjection(order, now);
        context.WorkOrders.Add(order);
        context.WorkOrderAudit.Add(new WorkOrderAudit
        {
            AuditId = Guid.NewGuid(),
            WorkOrderId = order.WorkOrderId,
            Action = "CreatedFromPreventivePlan",
            Changes = $"Plan={plan.MaintenancePlanNumber}; WorkOrder={order.WorkOrderNumber}",
            ChangedBy = Environment.UserName,
            ChangedAtUtc = now
        });
        context.ActivityHistory.Add(ActivityHistoryService.CreateEntry(new ActivityHistoryDraft(
            ActivityTypes.WorkOrderCreated, ActivityRecordTypes.WorkOrder, order.WorkOrderId, order.WorkOrderNumber,
            order.Site, order.Location, order.Title, "Work Order generated from Preventive Maintenance.", 1,
            $"PreventiveMaintenance={plan.MaintenancePlanNumber}")));
        if (order.Status == WorkOrderStatus.Assigned)
            context.ActivityHistory.Add(ActivityHistoryService.CreateEntry(new ActivityHistoryDraft(
                ActivityTypes.WorkOrderAssigned, ActivityRecordTypes.WorkOrder, order.WorkOrderId, order.WorkOrderNumber,
                order.Site, order.Location, order.Title, "Work Order assigned.", 1, $"AssignedTo={order.AssignedTo}")));

        plan.Revision = Math.Max(plan.Revision, 1) + 1;
        plan.UpdatedAtUtc = now;
        var auditChanges = $"WorkOrder={order.WorkOrderNumber}; DueDate={plan.NextDueDate:yyyy-MM-dd}";
        context.MaintenancePlanAudit.Add(CreateAudit(plan, "WorkOrderGenerated", auditChanges));
        context.ActivityHistory.Add(CreateActivity(plan, ActivityTypes.PreventivePlanGeneratedWorkOrder, "Work Order generated from Preventive Maintenance.", auditChanges));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return order;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await using var reload = contextFactory.CreateMasterDbContext();
            var raced = await reload.WorkOrders.AsNoTracking()
                .SingleOrDefaultAsync(item => item.MaintenancePlanId == maintenancePlanId
                    && item.Status != WorkOrderStatus.Completed
                    && item.Status != WorkOrderStatus.Cancelled, cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null) return raced;
            throw;
        }
    }

    public async Task<int> GetDueCountAsync(DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.MaintenancePlans.AsNoTracking()
            .CountAsync(item => item.IsActive && item.NextDueDate <= asOfDate, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ProcessDueActivitiesAsync(DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        await using var readContext = contextFactory.CreateMasterDbContext();
        var plans = await readContext.MaintenancePlans.AsNoTracking()
            .Where(item => item.IsActive && item.NextDueDate <= asOfDate)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dueMarker = $"DueDate={plan.NextDueDate:yyyy-MM-dd}";
            await using var writeContext = contextFactory.CreateMasterDbContext();
            await using var transaction = await writeContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var inserted = await writeContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT OR IGNORE INTO PreventiveDueOccurrence(MaintenancePlanId, OccurrenceKey, RecordedAtUtcTicks)
                VALUES ({plan.MaintenancePlanId.ToString("D")}, {dueMarker}, {DateTimeOffset.UtcNow.UtcTicks});
                """,
                cancellationToken).ConfigureAwait(false);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            writeContext.ActivityHistory.Add(CreateActivity(plan, ActivityTypes.PreventivePlanDue, "Preventive Maintenance is due.", dueMarker));
            await writeContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Validate(MaintenancePlanDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.PlanName)) throw new ArgumentException("Preventive maintenance subject is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Instructions)) throw new ArgumentException("Preventive maintenance task is required.", nameof(draft));
        if (draft.FrequencyValue <= 0) throw new ArgumentException("Frequency must be greater than zero.", nameof(draft));
        if (draft.AssetId == Guid.Empty) throw new ArgumentException("Asset ID is invalid.", nameof(draft));
        ExactNumericStorage.ValidateMoney(draft.EstimatedCost, nameof(draft));
    }

    private static async Task<Asset?> ResolveAssetAsync(MasterDbContext context, Guid? assetId, CancellationToken cancellationToken)
    {
        if (!assetId.HasValue) return null;
        return await context.Asset.AsNoTracking().SingleOrDefaultAsync(item => item.AssetId == assetId.Value && !item.IsArchived, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected asset was not found.");
    }

    private static MaintenanceType NormalizeType(MaintenanceType type) =>
        type is MaintenanceType.AssetAdded or MaintenanceType.AssetImported or MaintenanceType.AssetInformationUpdated or MaintenanceType.Note
            ? MaintenanceType.PreventiveMaintenance : type;

    private static ActivityHistoryEntry CreateActivity(MaintenancePlan plan, string activityType, string description, string changes) =>
        ActivityHistoryService.CreateEntry(new ActivityHistoryDraft(activityType, ActivityRecordTypes.PreventiveMaintenance, plan.MaintenancePlanId,
            plan.MaintenancePlanNumber, plan.Site, plan.Location, plan.PlanName, description, Math.Max(plan.Revision, 1), changes));

    private static MaintenancePlanAudit CreateAudit(MaintenancePlan plan, string action, string changes) => new()
    {
        AuditId = Guid.NewGuid(),
        MaintenancePlanId = plan.MaintenancePlanId,
        Action = action,
        Changes = changes,
        ChangedBy = Environment.UserName,
        ChangedAtUtc = DateTimeOffset.UtcNow
    };

    private static string Snapshot(MaintenancePlan plan) => JsonSerializer.Serialize(new
    {
        plan.AssetId,
        plan.AssetNumber,
        plan.Site,
        plan.Location,
        plan.PlanName,
        plan.MaintenanceCategory,
        plan.MaintenanceType,
        plan.Instructions,
        plan.FrequencyValue,
        plan.FrequencyUnit,
        plan.LastCompletedDate,
        plan.NextDueDate,
        plan.AssignedTo,
        plan.EstimatedCost,
        plan.IsActive
    });

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqliteException
        && sqliteException.SqliteExtendedErrorCode == 2067;

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
