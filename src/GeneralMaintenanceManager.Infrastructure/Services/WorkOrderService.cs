using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class WorkOrderService(
    DatabaseContextFactory contextFactory,
    IMaintenanceRecordService maintenanceRecordService) : IWorkOrderService
{
    private const int MaximumPageSize = 200;
    private const int MaximumSuggestionsPerField = 500;
    private const int SuggestionSampleLimit = 1_000;
    private MaintenanceEntrySuggestionCatalog? _suggestionCache;
    private long _suggestionGeneration;

    public async Task<WorkOrderPage> GetPageAsync(WorkOrderQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ReferenceYear.HasValue) DataPaths.ValidateYear(query.ReferenceYear.Value);
        if (query.DueFromInclusive.HasValue && query.DueToInclusive.HasValue && query.DueFromInclusive > query.DueToInclusive)
            throw new ArgumentException("Work Order due-date range is invalid.", nameof(query));
        if (query.ActivityFromInclusive.HasValue && query.ActivityToInclusive.HasValue && query.ActivityFromInclusive > query.ActivityToInclusive)
            throw new ArgumentException("Work Order activity-date range is invalid.", nameof(query));

        await using var context = contextFactory.CreateMasterDbContext();
        IQueryable<WorkOrder> rows;
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var ftsQuery = BuildFtsQuery(query.SearchText);
            rows = context.WorkOrders.FromSqlInterpolated($$"""
                SELECT w.*
                FROM WorkOrders AS w
                INNER JOIN WorkOrderSearch AS f ON f.rowid = w.rowid
                WHERE WorkOrderSearch MATCH {{ftsQuery}}
                """).AsNoTracking();
        }
        else
        {
            rows = context.WorkOrders.AsNoTracking();
        }

        if (query.Status.HasValue) rows = rows.Where(item => item.Status == query.Status.Value);
        if (query.Priority.HasValue) rows = rows.Where(item => item.Priority == query.Priority.Value);
        if (query.IsClosed.HasValue) rows = rows.Where(item => item.IsClosed == query.IsClosed.Value);
        if (query.ReferenceYear.HasValue) rows = rows.Where(item => item.ReferenceYear == query.ReferenceYear.Value);
        if (!string.IsNullOrWhiteSpace(query.AssignedTo))
        {
            var value = query.AssignedTo.Trim();
            rows = rows.Where(item => item.AssignedTo == value);
        }
        if (!string.IsNullOrWhiteSpace(query.PerformedBy))
        {
            var value = query.PerformedBy.Trim();
            rows = rows.Where(item => item.PerformedBy == value);
        }
        if (!string.IsNullOrWhiteSpace(query.Site))
        {
            var value = query.Site.Trim();
            rows = rows.Where(item => item.Site == value);
        }
        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            var value = query.Location.Trim();
            rows = rows.Where(item => item.Location == value);
        }
        if (query.DueFromInclusive.HasValue)
        {
            var day = query.DueFromInclusive.Value.DayNumber;
            rows = rows.Where(item => item.SortDueDateOrdinal >= day && item.SortDueDateOrdinal != int.MaxValue);
        }
        if (query.DueToInclusive.HasValue)
        {
            var day = query.DueToInclusive.Value.DayNumber;
            rows = rows.Where(item => item.SortDueDateOrdinal <= day);
        }
        if (query.ActivityFromInclusive.HasValue)
        {
            var ticks = query.ActivityFromInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.ActivityAtUtcTicks >= ticks);
        }
        if (query.ActivityToInclusive.HasValue)
        {
            var ticks = query.ActivityToInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.ActivityAtUtcTicks <= ticks);
        }
        if (query.Cursor is not null)
        {
            var cursor = query.Cursor;
            var cursorPriority = (int)cursor.Priority;
            rows = rows.Where(item =>
                (!cursor.IsClosed && item.IsClosed)
                || (item.IsClosed == cursor.IsClosed && (int)item.Priority < cursorPriority)
                || (item.IsClosed == cursor.IsClosed && (int)item.Priority == cursorPriority
                    && item.SortDueDateOrdinal > cursor.SortDueDateOrdinal)
                || (item.IsClosed == cursor.IsClosed && (int)item.Priority == cursorPriority
                    && item.SortDueDateOrdinal == cursor.SortDueDateOrdinal
                    && item.ReportedAtUtcTicks < cursor.ReportedAtUtcTicks)
                || (item.IsClosed == cursor.IsClosed && (int)item.Priority == cursorPriority
                    && item.SortDueDateOrdinal == cursor.SortDueDateOrdinal
                    && item.ReportedAtUtcTicks == cursor.ReportedAtUtcTicks
                    && item.ReferenceSequence < cursor.ReferenceSequence));
        }

        var take = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var materialized = await rows
            .OrderBy(item => item.IsClosed)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.SortDueDateOrdinal)
            .ThenByDescending(item => item.ReportedAtUtcTicks)
            .ThenByDescending(item => item.ReferenceSequence)
            .Take(take + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = materialized.Length > take;
        var items = materialized.Take(take).ToArray();
        var last = items.LastOrDefault();
        var next = hasMore && last is not null
            ? new WorkOrderPageCursor(last.IsClosed, last.Priority, last.SortDueDateOrdinal, last.ReportedAtUtcTicks, last.ReferenceSequence)
            : null;
        return new WorkOrderPage(items, next, hasMore);
    }

    public async Task<MaintenanceEntrySuggestionCatalog> GetEntrySuggestionCatalogAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var cached = Volatile.Read(ref _suggestionCache);
            if (cached is not null) return cached;

            var generation = Interlocked.Read(ref _suggestionGeneration);
            var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WorkOrderPageCursor? cursor = null;
            var sampled = 0;
            while (sampled < SuggestionSampleLimit)
            {
                var page = await GetPageAsync(new WorkOrderQuery(
                    PageSize: Math.Min(MaximumPageSize, SuggestionSampleLimit - sampled),
                    Cursor: cursor), cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    AddSuggestion(sites, item.Site);
                    AddSuggestion(locations, item.Location);
                    AddSuggestion(subjects, item.Title);
                }
                sampled += page.Items.Count;
                if (!page.HasMore || page.NextCursor is null || page.NextCursor == cursor) break;
                cursor = page.NextCursor;
            }
            var catalog = new MaintenanceEntrySuggestionCatalog(
                NormalizeSuggestions(sites), NormalizeSuggestions(locations), NormalizeSuggestions(subjects));

            if (generation == Interlocked.Read(ref _suggestionGeneration))
            {
                Volatile.Write(ref _suggestionCache, catalog);
                return catalog;
            }

            // A write invalidated the catalog while it was being built. Re-read instead
            // of publishing a stale snapshot. Multiple cold-cache callers may duplicate
            // this bounded read, but no disposable synchronization object is required.
        }
    }

    public void InvalidateSuggestionCache()
    {
        Interlocked.Increment(ref _suggestionGeneration);
        Volatile.Write(ref _suggestionCache, null);
    }

    public async Task<WorkOrderDashboardMetrics> GetDashboardMetricsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0),
                (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0 AND Priority = $urgent),
                (
                    (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0 AND Priority = $low AND SortDueDateOrdinal < $today)
                  + (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0 AND Priority = $normal AND SortDueDateOrdinal < $today)
                  + (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0 AND Priority = $high AND SortDueDateOrdinal < $today)
                  + (SELECT COUNT(*) FROM WorkOrders WHERE IsClosed = 0 AND Priority = $urgent AND SortDueDateOrdinal < $today)
                );
            """;
        AddParameter(command, "$low", (int)WorkOrderPriority.Low);
        AddParameter(command, "$normal", (int)WorkOrderPriority.Normal);
        AddParameter(command, "$high", (int)WorkOrderPriority.High);
        AddParameter(command, "$urgent", (int)WorkOrderPriority.Urgent);
        var today = command.CreateParameter();
        today.ParameterName = "$today";
        today.Value = DateOnly.FromDateTime(DateTime.Today).DayNumber;
        command.Parameters.Add(today);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new WorkOrderDashboardMetrics(0, 0, 0);
        return new WorkOrderDashboardMetrics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<WorkOrder?> GetByIdAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.WorkOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkOrderId == workOrderId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkOrder?> GetByNumberAsync(string workOrderNumber, CancellationToken cancellationToken = default)
    {
        var normalized = Clean(workOrderNumber).ToUpperInvariant();
        if (normalized.Length == 0) return null;
        await using var context = contextFactory.CreateMasterDbContext();
        return await context.WorkOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => EF.Functions.Collate(item.WorkOrderNumber, "NOCASE") == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkOrder> CreateAsync(WorkOrderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateCreate(draft);

        await using var context = contextFactory.CreateMasterDbContext();
        var asset = await ResolveAssetAsync(context, draft.AssetId, cancellationToken).ConfigureAwait(false);
        var (site, location) = ResolveLocation(draft.Site, draft.Location, asset);

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var number = await ReferenceNumberAllocator.ReserveWorkOrderAsync(context, now.Year, cancellationToken).ConfigureAwait(false);
        var order = new WorkOrder
        {
            WorkOrderId = Guid.NewGuid(),
            WorkOrderNumber = number,
            AssetId = asset?.AssetId,
            AssetNumber = asset?.AssetNumber ?? string.Empty,
            MaintenancePlanId = draft.MaintenancePlanId,
            Site = site,
            Location = location,
            Title = Clean(draft.Title),
            MaintenanceCategory = Clean(draft.MaintenanceCategory),
            MaintenanceType = NormalizeMaintenanceType(draft.MaintenanceType),
            Priority = draft.Priority,
            ProblemDescription = Clean(draft.ProblemDescription),
            Notes = Clean(draft.Notes),
            RequestedBy = Clean(draft.RequestedBy),
            ReportedAtUtc = now,
            DueDate = draft.DueDate,
            Status = Clean(draft.AssignedTo).Length == 0 ? WorkOrderStatus.New : WorkOrderStatus.Assigned,
            AssignedTo = Clean(draft.AssignedTo),
            CreatedBy = Environment.UserName,
            CreatedAtUtc = now,
            Revision = 1
        };
        UpdateQueryProjection(order, now);
        context.WorkOrders.Add(order);
        context.WorkOrderAudit.Add(CreateAudit(order.WorkOrderId, "Created", string.Empty, $"Created {number}."));
        context.ActivityHistory.Add(CreateActivity(order, ActivityTypes.WorkOrderCreated, "Work order created.", $"Priority={order.Priority}"));
        if (order.Status == WorkOrderStatus.Assigned)
            context.ActivityHistory.Add(CreateActivity(order, ActivityTypes.WorkOrderAssigned, "Work order assigned.", $"AssignedTo={order.AssignedTo}"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return order;
    }

    public async Task UpdateAsync(Guid workOrderId, WorkOrderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (workOrderId == Guid.Empty) throw new ArgumentException("Work order ID is required.", nameof(workOrderId));
        ValidateCreate(draft);

        await using var context = contextFactory.CreateMasterDbContext();
        var order = await context.WorkOrders.SingleOrDefaultAsync(item => item.WorkOrderId == workOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Work order was not found.");
        EnsureNotClosed(order);
        await EnsureNoPendingCompletionAsync(context, workOrderId, cancellationToken).ConfigureAwait(false);

        if (draft.MaintenancePlanId != order.MaintenancePlanId)
            throw new InvalidOperationException("The Preventive Maintenance source of a Work Order is immutable. Create a new Work Order if the source plan is wrong.");
        if (!string.Equals(Clean(draft.AssignedTo), Clean(order.AssignedTo), StringComparison.Ordinal))
            throw new InvalidOperationException("Use the Assign action to change a Work Order assignee.");
        if ((order.Status is WorkOrderStatus.InProgress or WorkOrderStatus.OnHold) && draft.AssetId != order.AssetId)
            throw new InvalidOperationException("An active Work Order cannot be reassigned to a different Asset through generic editing.");

        var asset = await ResolveAssetAsync(context, draft.AssetId, cancellationToken).ConfigureAwait(false);
        var (site, location) = ResolveLocation(draft.Site, draft.Location, asset);

        var before = WorkOrderEditableSnapshot(order);
        order.AssetId = asset?.AssetId;
        order.AssetNumber = asset?.AssetNumber ?? string.Empty;
        order.Site = site;
        order.Location = location;
        order.Title = Clean(draft.Title);
        order.MaintenanceCategory = Clean(draft.MaintenanceCategory);
        order.MaintenanceType = NormalizeMaintenanceType(draft.MaintenanceType);
        order.Priority = draft.Priority;
        order.ProblemDescription = Clean(draft.ProblemDescription);
        order.Notes = Clean(draft.Notes);
        order.RequestedBy = Clean(draft.RequestedBy);
        order.DueDate = draft.DueDate;
        var after = WorkOrderEditableSnapshot(order);
        if (string.Equals(before, after, StringComparison.Ordinal)) return;

        order.Revision = Math.Max(order.Revision, 1) + 1;
        order.LastModifiedAtUtc = DateTimeOffset.UtcNow;
        order.LastModifiedBy = Environment.UserName;
        UpdateQueryProjection(order, order.LastModifiedAtUtc.Value);
        context.WorkOrderAudit.Add(CreateAudit(order.WorkOrderId, "Updated", "Work order details updated.", JsonSerializer.Serialize(new { Before = before, After = after })));
        context.ActivityHistory.Add(CreateActivity(order, ActivityTypes.WorkOrderEdited, "Work order details updated.", "EditableFieldsChanged=true"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
    }

    public async Task AssignAsync(Guid workOrderId, string assignedTo, CancellationToken cancellationToken = default)
    {
        var assignee = Clean(assignedTo);
        if (assignee.Length == 0) throw new ArgumentException("Assigned technician/person is required.", nameof(assignedTo));
        await MutateAsync(workOrderId, order =>
        {
            EnsureNotClosed(order);
            if (order.Status == WorkOrderStatus.InProgress)
                throw new InvalidOperationException("An in-progress work order must be put on hold before reassignment.");
            if (string.Equals(order.AssignedTo, assignee, StringComparison.Ordinal) && order.Status == WorkOrderStatus.Assigned)
                return null;
            order.AssignedTo = assignee;
            order.Status = WorkOrderStatus.Assigned;
            return ("Assigned", $"AssignedTo={assignee}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(workOrderId, order =>
        {
            EnsureNotClosed(order);
            if (order.Status != WorkOrderStatus.Assigned)
                throw new InvalidOperationException("Only an assigned work order can be started. Resume an on-hold work order explicitly.");
            if (string.IsNullOrWhiteSpace(order.AssignedTo))
                throw new InvalidOperationException("An assignee is required before starting work.");
            order.Status = WorkOrderStatus.InProgress;
            order.StartedAtUtc ??= DateTimeOffset.UtcNow;
            return ("Started", "Status=InProgress");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(workOrderId, order =>
        {
            EnsureNotClosed(order);
            if (order.Status != WorkOrderStatus.OnHold)
                throw new InvalidOperationException("Only an on-hold work order can be resumed.");
            if (string.IsNullOrWhiteSpace(order.AssignedTo))
                throw new InvalidOperationException("An assignee is required before resuming work.");
            order.Status = WorkOrderStatus.InProgress;
            order.StartedAtUtc ??= DateTimeOffset.UtcNow;
            return ("Resumed", "Status=InProgress");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutOnHoldAsync(Guid workOrderId, string reason, CancellationToken cancellationToken = default)
    {
        var normalizedReason = Clean(reason);
        if (normalizedReason.Length == 0) throw new ArgumentException("A hold reason is required.", nameof(reason));
        await MutateAsync(workOrderId, order =>
        {
            EnsureNotClosed(order);
            if (order.Status != WorkOrderStatus.InProgress)
                throw new InvalidOperationException("Only an in-progress work order can be put on hold.");
            order.Status = WorkOrderStatus.OnHold;
            return ("OnHold", normalizedReason);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(WorkOrderCompletionDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.WorkOrderId == Guid.Empty) throw new ArgumentException("Work order ID is required.", nameof(draft));

        var intent = await PrepareCompletionIntentAsync(draft, cancellationToken).ConfigureAwait(false);
        if (intent.State == WorkOrderCompletionIntentStates.Finalized) return;
        await CompleteIntentAsync(intent, recovered: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverPendingCompletionsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        var pendingIds = await context.WorkOrderCompletionIntents.AsNoTracking()
            .Where(item => item.State != WorkOrderCompletionIntentStates.Finalized)
            .Select(item => item.WorkOrderCompletionIntentId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var intentId in pendingIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var reload = contextFactory.CreateMasterDbContext();
            var intent = await reload.WorkOrderCompletionIntents.AsNoTracking()
                .SingleAsync(item => item.WorkOrderCompletionIntentId == intentId, cancellationToken)
                .ConfigureAwait(false);
            await CompleteIntentAsync(intent, recovered: true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(Guid workOrderId, string reason, CancellationToken cancellationToken = default)
    {
        var normalizedReason = Clean(reason);
        if (normalizedReason.Length == 0) throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        await MutateAsync(workOrderId, order =>
        {
            EnsureNotClosed(order);
            order.Status = WorkOrderStatus.Cancelled;
            order.CompletedAtUtc = DateTimeOffset.UtcNow;
            return ("Cancelled", normalizedReason);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkOrderCompletionIntent> PrepareCompletionIntentAsync(WorkOrderCompletionDraft draft, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var order = await context.WorkOrders.SingleOrDefaultAsync(item => item.WorkOrderId == draft.WorkOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Work order was not found.");

        var existingIntent = await context.WorkOrderCompletionIntents
            .SingleOrDefaultAsync(item => item.WorkOrderId == draft.WorkOrderId, cancellationToken)
            .ConfigureAwait(false);
        if (existingIntent is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingIntent;
        }

        if (order.Status == WorkOrderStatus.Completed && !string.IsNullOrWhiteSpace(order.GeneratedMaintenanceNumber))
            throw new InvalidOperationException("The completed Work Order already has a generated Maintenance reference but no recovery intent. Stop and audit this development database before changing history.");
        EnsureNotClosed(order);
        if (string.IsNullOrWhiteSpace(draft.WorkPerformed)) throw new ArgumentException("Work performed is required.", nameof(draft));
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
        ExactNumericStorage.ValidateDowntimeHours(draft.DowntimeHours, nameof(draft));

        Asset? asset = null;
        if (order.AssetId.HasValue)
        {
            asset = await context.Asset.AsNoTracking().SingleOrDefaultAsync(item => item.AssetId == order.AssetId.Value, cancellationToken).ConfigureAwait(false);
        }

        var completionUtc = DateTimeOffset.UtcNow;
        var occurredAt = completionUtc.ToLocalTime();
        var maintenanceNumber = await ReferenceNumberAllocator.ReserveMaintenanceAsync(context, occurredAt.Year, cancellationToken).ConfigureAwait(false);
        if (!MaintenanceReference.TryParse(maintenanceNumber, out var referenceYear, out var referenceSequence))
            throw new InvalidOperationException("The reserved Maintenance reference is invalid.");

        var intent = new WorkOrderCompletionIntent
        {
            WorkOrderCompletionIntentId = Guid.NewGuid(),
            WorkOrderId = order.WorkOrderId,
            MaintenanceRecordId = Guid.NewGuid(),
            MaintenanceNumber = maintenanceNumber,
            ReferenceYear = referenceYear,
            ReferenceSequence = referenceSequence,
            OccurredAt = occurredAt,
            Site = order.Site,
            Location = order.Location,
            Subject = order.Title,
            MaintenanceType = GetGeneratedMaintenanceTypeName(order),
            AssetId = order.AssetId,
            AssetNumberSnapshot = asset?.AssetNumber ?? order.AssetNumber,
            AssetNameSnapshot = asset?.AssetName ?? string.Empty,
            WorkOrderNumberSnapshot = order.WorkOrderNumber,
            WorkPerformed = Clean(draft.WorkPerformed),
            PerformedBy = Clean(draft.PerformedBy),
            Cost = draft.Cost,
            DowntimeHours = draft.DowntimeHours,
            MaterialsOrPartsNotes = Clean(draft.MaterialsOrPartsNotes),
            ConditionAfter = Clean(draft.ConditionAfter),
            OperationalStatusAfter = Clean(draft.OperationalStatusAfter),
            CompletionNotes = Clean(draft.CompletionNotes),
            MaintenanceNotes = JoinNotes(order.Notes, draft.MaterialsOrPartsNotes, draft.CompletionNotes),
            State = WorkOrderCompletionIntentStates.Reserved,
            CreatedAtUtc = completionUtc,
            UpdatedAtUtc = completionUtc
        };
        context.WorkOrderCompletionIntents.Add(intent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent;
    }

    private async Task CompleteIntentAsync(WorkOrderCompletionIntent intent, bool recovered, CancellationToken cancellationToken)
    {
        var maintenanceRecord = await maintenanceRecordService.EnsureWorkOrderMaintenanceAsync(ToGeneratedMaintenanceDraft(intent), cancellationToken).ConfigureAwait(false);
        await MarkAnnualWrittenAsync(intent.WorkOrderCompletionIntentId, maintenanceRecord, cancellationToken).ConfigureAwait(false);
        await FinalizeMasterCompletionAsync(intent.WorkOrderCompletionIntentId, maintenanceRecord, recovered, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkAnnualWrittenAsync(Guid intentId, MaintenanceRecord maintenanceRecord, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        var intent = await context.WorkOrderCompletionIntents.SingleAsync(item => item.WorkOrderCompletionIntentId == intentId, cancellationToken).ConfigureAwait(false);
        if (intent.State == WorkOrderCompletionIntentStates.Finalized) return;
        if (intent.MaintenanceRecordId != maintenanceRecord.MaintenanceRecordId
            || !string.Equals(intent.MaintenanceNumber, maintenanceRecord.MaintenanceNumber, StringComparison.OrdinalIgnoreCase)
            || intent.ReferenceYear != maintenanceRecord.ReferenceYear)
            throw new InvalidOperationException("Annual Maintenance identity does not match the durable completion intent.");
        intent.State = WorkOrderCompletionIntentStates.AnnualWritten;
        intent.AnnualWrittenAtUtc ??= DateTimeOffset.UtcNow;
        intent.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
    }

    private async Task FinalizeMasterCompletionAsync(Guid intentId, MaintenanceRecord maintenanceRecord, bool recovered, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateMasterDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var intent = await context.WorkOrderCompletionIntents.SingleAsync(item => item.WorkOrderCompletionIntentId == intentId, cancellationToken).ConfigureAwait(false);
        var order = await context.WorkOrders.SingleAsync(item => item.WorkOrderId == intent.WorkOrderId, cancellationToken).ConfigureAwait(false);

        if (intent.State == WorkOrderCompletionIntentStates.Finalized)
        {
            if (order.GeneratedMaintenanceRecordId != intent.MaintenanceRecordId
                || !string.Equals(order.GeneratedMaintenanceNumber, intent.MaintenanceNumber, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Finalized completion intent does not match the Work Order generated Maintenance reference.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (order.Status == WorkOrderStatus.Cancelled)
            throw new InvalidOperationException("A Work Order with a reserved completion intent cannot be cancelled.");

        var wasCompleted = order.Status == WorkOrderStatus.Completed;
        if (wasCompleted && order.GeneratedMaintenanceRecordId.HasValue && order.GeneratedMaintenanceRecordId != maintenanceRecord.MaintenanceRecordId)
            throw new InvalidOperationException("Work Order completion is already linked to a different Maintenance record.");

        order.Status = WorkOrderStatus.Completed;
        var completedAtUtc = intent.OccurredAt.ToUniversalTime();
        order.StartedAtUtc ??= completedAtUtc;
        order.CompletedAtUtc = completedAtUtc;
        order.WorkPerformed = intent.WorkPerformed;
        order.PerformedBy = intent.PerformedBy;
        order.Cost = intent.Cost;
        order.DowntimeHours = intent.DowntimeHours;
        order.MaterialsOrPartsNotes = intent.MaterialsOrPartsNotes;
        order.ConditionAfter = intent.ConditionAfter;
        order.OperationalStatusAfter = intent.OperationalStatusAfter;
        order.CompletionNotes = intent.CompletionNotes;
        order.GeneratedMaintenanceRecordId = maintenanceRecord.MaintenanceRecordId;
        order.GeneratedMaintenanceNumber = maintenanceRecord.MaintenanceNumber;
        order.GeneratedMaintenanceYear = maintenanceRecord.ReferenceYear;
        order.Revision = Math.Max(order.Revision, 1) + 1;
        order.LastModifiedAtUtc = DateTimeOffset.UtcNow;
        order.LastModifiedBy = Environment.UserName;
        UpdateQueryProjection(order, order.LastModifiedAtUtc.Value);

        if (!wasCompleted && order.AssetId.HasValue)
        {
            var asset = await context.Asset.SingleOrDefaultAsync(item => item.AssetId == order.AssetId.Value && !item.IsArchived, cancellationToken).ConfigureAwait(false);
            if (asset is not null)
            {
                var beforeCondition = asset.Condition;
                var beforeStatus = asset.OperationalStatus;
                if (intent.ConditionAfter.Length > 0) asset.Condition = intent.ConditionAfter;
                if (intent.OperationalStatusAfter.Length > 0) asset.OperationalStatus = intent.OperationalStatusAfter;
                if (!string.Equals(beforeCondition, asset.Condition, StringComparison.Ordinal)
                    || !string.Equals(beforeStatus, asset.OperationalStatus, StringComparison.Ordinal))
                {
                    asset.UpdatedAtUtc = completedAtUtc;
                    context.AssetAudit.Add(new AssetAudit
                    {
                        AuditId = Guid.NewGuid(),
                        AssetId = asset.AssetId,
                        Action = "WorkOrderCompletionStateChange",
                        Reason = order.WorkOrderNumber,
                        Changes = JsonSerializer.Serialize(new
                        {
                            WorkOrder = order.WorkOrderNumber,
                            Maintenance = maintenanceRecord.MaintenanceNumber,
                            Before = new { Condition = beforeCondition, OperationalStatus = beforeStatus },
                            After = new { asset.Condition, asset.OperationalStatus }
                        }),
                        ChangedBy = Environment.UserName,
                        ChangedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        if (!wasCompleted && order.MaintenancePlanId.HasValue)
        {
            var plan = await context.MaintenancePlans.SingleOrDefaultAsync(item => item.MaintenancePlanId == order.MaintenancePlanId.Value, cancellationToken).ConfigureAwait(false);
            if (plan is not null)
            {
                var completedDate = DateOnly.FromDateTime(intent.OccurredAt.LocalDateTime);
                var previousDueDate = plan.NextDueDate;
                plan.LastCompletedDate = completedDate;
                plan.NextDueDate = CalculateNextDueDate(completedDate, plan.FrequencyValue, plan.FrequencyUnit);
                plan.Revision = Math.Max(plan.Revision, 1) + 1;
                plan.UpdatedAtUtc = completedAtUtc;
                context.MaintenancePlanAudit.Add(new MaintenancePlanAudit
                {
                    AuditId = Guid.NewGuid(),
                    MaintenancePlanId = plan.MaintenancePlanId,
                    Action = "OccurrenceCompleted",
                    Changes = JsonSerializer.Serialize(new
                    {
                        WorkOrder = order.WorkOrderNumber,
                        Maintenance = maintenanceRecord.MaintenanceNumber,
                        Completed = completedDate,
                        PreviousDue = previousDueDate,
                        NextDue = plan.NextDueDate
                    }),
                    ChangedBy = Environment.UserName,
                    ChangedAtUtc = DateTimeOffset.UtcNow
                });
                context.ActivityHistory.Add(ActivityHistoryService.CreateEntry(new ActivityHistoryDraft(
                    ActivityTypes.PreventivePlanCompletedOccurrence,
                    ActivityRecordTypes.PreventiveMaintenance,
                    plan.MaintenancePlanId,
                    plan.MaintenancePlanNumber,
                    plan.Site,
                    plan.Location,
                    plan.PlanName,
                    "Preventive Maintenance occurrence completed through its Work Order.",
                    plan.Revision,
                    $"WorkOrder={order.WorkOrderNumber}; Maintenance={maintenanceRecord.MaintenanceNumber}; Completed={completedDate:yyyy-MM-dd}; PreviousDue={previousDueDate:yyyy-MM-dd}; NextDue={plan.NextDueDate:yyyy-MM-dd}")));
            }
        }

        var action = recovered || wasCompleted ? "CompletionRecovered" : "Completed";
        context.WorkOrderAudit.Add(CreateAudit(order.WorkOrderId, action, string.Empty,
            $"Maintenance={maintenanceRecord.MaintenanceNumber}; MaintenanceRecordId={maintenanceRecord.MaintenanceRecordId:D}; Year={maintenanceRecord.ReferenceYear}"));
        context.ActivityHistory.Add(CreateActivity(order,
            recovered || wasCompleted ? ActivityTypes.WorkOrderCompletionRecovered : ActivityTypes.WorkOrderCompleted,
            recovered || wasCompleted ? "Work Order completion recovered." : "Work Order completed.",
            $"Maintenance={maintenanceRecord.MaintenanceNumber}; PerformedBy={order.PerformedBy}; Cost={order.Cost}"));

        intent.State = WorkOrderCompletionIntentStates.Finalized;
        intent.AnnualWrittenAtUtc ??= DateTimeOffset.UtcNow;
        intent.FinalizedAtUtc = DateTimeOffset.UtcNow;
        intent.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MutateAsync(Guid workOrderId, Func<WorkOrder, (string Action, string Reason)?> mutation, CancellationToken cancellationToken)
    {
        if (workOrderId == Guid.Empty) throw new ArgumentException("Work order ID is required.", nameof(workOrderId));
        await using var context = contextFactory.CreateMasterDbContext();
        var order = await context.WorkOrders.SingleOrDefaultAsync(item => item.WorkOrderId == workOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Work order was not found.");
        await EnsureNoPendingCompletionAsync(context, workOrderId, cancellationToken).ConfigureAwait(false);
        var result = mutation(order);
        if (result is null) return;
        order.Revision = Math.Max(order.Revision, 1) + 1;
        order.LastModifiedAtUtc = DateTimeOffset.UtcNow;
        order.LastModifiedBy = Environment.UserName;
        UpdateQueryProjection(order, order.LastModifiedAtUtc.Value);
        context.WorkOrderAudit.Add(CreateAudit(order.WorkOrderId, result.Value.Action, result.Value.Reason, result.Value.Reason));
        context.ActivityHistory.Add(CreateActivity(order, MapActivityType(result.Value.Action), LifecycleDescription(result.Value.Action), result.Value.Reason));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
    }

    private static async Task EnsureNoPendingCompletionAsync(MasterDbContext context, Guid workOrderId, CancellationToken cancellationToken)
    {
        var pending = await context.WorkOrderCompletionIntents.AsNoTracking().AnyAsync(
            item => item.WorkOrderId == workOrderId && item.State != WorkOrderCompletionIntentStates.Finalized,
            cancellationToken).ConfigureAwait(false);
        if (pending) throw new InvalidOperationException("This Work Order has a pending completion recovery. Complete recovery before changing its lifecycle.");
    }

    private static async Task<Asset?> ResolveAssetAsync(MasterDbContext context, Guid? assetId, CancellationToken cancellationToken)
    {
        if (!assetId.HasValue) return null;
        if (assetId.Value == Guid.Empty) throw new ArgumentException("Asset ID is invalid.", nameof(assetId));
        return await context.Asset.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AssetId == assetId.Value && !item.IsArchived, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected asset was not found.");
    }

    private static (string Site, string Location) ResolveLocation(string? siteValue, string? locationValue, Asset? asset)
    {
        var site = Clean(siteValue);
        var location = Clean(locationValue);
        if (asset is not null)
        {
            if (site.Length == 0) site = asset.Site;
            if (location.Length == 0) location = asset.Location;
        }
        if (location.Length == 0) throw new ArgumentException("Location is required for a work order.", nameof(locationValue));
        return (site, location);
    }

    private static WorkOrderGeneratedMaintenanceDraft ToGeneratedMaintenanceDraft(WorkOrderCompletionIntent intent) => new(
        intent.WorkOrderId,
        intent.MaintenanceRecordId,
        intent.MaintenanceNumber,
        intent.ReferenceYear,
        intent.ReferenceSequence,
        intent.OccurredAt,
        intent.Site,
        intent.Location,
        intent.Subject,
        intent.MaintenanceType,
        intent.WorkPerformed,
        intent.PerformedBy,
        intent.Cost,
        intent.MaintenanceNotes,
        intent.AssetId,
        intent.AssetNumberSnapshot,
        intent.AssetNameSnapshot,
        intent.WorkOrderNumberSnapshot,
        intent.DowntimeHours);

    internal static void UpdateQueryProjection(WorkOrder order, DateTimeOffset activityAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!WorkOrderReference.TryParse(order.WorkOrderNumber, out var year, out var sequence))
            throw new InvalidOperationException($"Work Order reference '{order.WorkOrderNumber}' is invalid.");
        order.ReferenceYear = year;
        order.ReferenceSequence = sequence;
        order.ReportedAtUtcTicks = order.ReportedAtUtc.UtcTicks;
        order.ActivityAtUtcTicks = activityAtUtc.UtcTicks;
        order.IsClosed = order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled;
        order.SortDueDateOrdinal = order.DueDate?.DayNumber ?? int.MaxValue;
    }

    private static void ValidateCreate(WorkOrderDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new ArgumentException("Work order subject is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ProblemDescription)) throw new ArgumentException("Problem / request description is required.", nameof(draft));
        if (draft.AssetId == Guid.Empty) throw new ArgumentException("Asset ID is invalid.", nameof(draft));
    }

    private static MaintenanceType NormalizeMaintenanceType(MaintenanceType type) =>
        type is MaintenanceType.AssetAdded or MaintenanceType.AssetImported or MaintenanceType.AssetInformationUpdated or MaintenanceType.Note
            ? MaintenanceType.GeneralMaintenance
            : type;

    private static void EnsureNotClosed(WorkOrder order)
    {
        if (order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
            throw new InvalidOperationException("The work order is already closed.");
    }

    private static string MapActivityType(string action) => action switch
    {
        "Assigned" => ActivityTypes.WorkOrderAssigned,
        "Started" => ActivityTypes.WorkOrderStarted,
        "Resumed" => ActivityTypes.WorkOrderResumed,
        "OnHold" => ActivityTypes.WorkOrderPutOnHold,
        "Cancelled" => ActivityTypes.WorkOrderCancelled,
        _ => ActivityTypes.WorkOrderEdited
    };

    private static string LifecycleDescription(string action) => action switch
    {
        "Assigned" => "Work Order assigned.",
        "Started" => "Work Order started.",
        "Resumed" => "Work Order resumed.",
        "OnHold" => "Work Order put on hold.",
        "Cancelled" => "Work Order cancelled.",
        _ => "Work Order changed."
    };

    private static ActivityHistoryEntry CreateActivity(WorkOrder order, string activityType, string description, string changes) =>
        ActivityHistoryService.CreateEntry(new ActivityHistoryDraft(
            activityType, ActivityRecordTypes.WorkOrder, order.WorkOrderId, order.WorkOrderNumber,
            order.Site, order.Location, order.Title, description, Math.Max(order.Revision, 1), changes));

    private static WorkOrderAudit CreateAudit(Guid workOrderId, string action, string reason, string changes) => new()
    {
        AuditId = Guid.NewGuid(),
        WorkOrderId = workOrderId,
        Action = action,
        Reason = reason,
        Changes = changes,
        ChangedBy = Environment.UserName,
        ChangedAtUtc = DateTimeOffset.UtcNow
    };

    internal static DateOnly CalculateNextDueDate(DateOnly from, int value, MaintenanceFrequencyUnit unit)
    {
        var normalized = Math.Max(1, value);
        return unit switch
        {
            MaintenanceFrequencyUnit.Days => from.AddDays(normalized),
            MaintenanceFrequencyUnit.Weeks => from.AddDays(normalized * 7),
            MaintenanceFrequencyUnit.Months => from.AddMonths(normalized),
            MaintenanceFrequencyUnit.Years => from.AddYears(normalized),
            _ => from.AddMonths(normalized)
        };
    }

    internal static string GetGeneratedMaintenanceTypeName(WorkOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var category = Clean(order.MaintenanceCategory);
        return order.MaintenancePlanId is null && category.Length > 0
            ? category
            : GetMaintenanceTypeName(order.MaintenanceType);
    }

    internal static string GetMaintenanceTypeName(MaintenanceType type) => type switch
    {
        MaintenanceType.CorrectiveMaintenance or MaintenanceType.Breakdown => "Repair",
        MaintenanceType.RoutineService or MaintenanceType.PreventiveMaintenance => "Service",
        MaintenanceType.Inspection => "Inspection",
        MaintenanceType.Installation => "Installation",
        MaintenanceType.Replacement => "Replacement",
        MaintenanceType.TestOrCalibration => "Test / Calibration",
        _ => "General Maintenance"
    };

    private static string WorkOrderEditableSnapshot(WorkOrder order) => JsonSerializer.Serialize(new
    {
        order.AssetId,
        order.AssetNumber,
        order.Site,
        order.Location,
        order.Title,
        order.MaintenanceCategory,
        order.MaintenanceType,
        order.Priority,
        order.ProblemDescription,
        order.Notes,
        order.RequestedBy,
        order.DueDate
    });

    private static string JoinNotes(params string[] values) =>
        string.Join(" | ", values.Select(Clean).Where(value => value.Length > 0));

    private static string BuildFtsQuery(string value)
    {
        var tokens = Regex.Matches(value ?? string.Empty, @"[\p{L}\p{N}_]+")
            .Select(match => match.Value)
            .Where(token => token.Length > 0)
            .Take(12)
            .Select(token => $"\"{token.Replace("\"", "\"\"")}\"*")
            .ToArray();
        if (tokens.Length == 0) return "\"__no_match__\"";
        return string.Join(" AND ", tokens);
    }

    private static string[] NormalizeSuggestions(IEnumerable<string> values) => values
        .Select(Clean)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
        .Take(MaximumSuggestionsPerField)
        .ToArray();

    private static void AddSuggestion(HashSet<string> values, string? value)
    {
        var normalized = Clean(value);
        if (normalized.Length > 0 && values.Count < MaximumSuggestionsPerField) values.Add(normalized);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
