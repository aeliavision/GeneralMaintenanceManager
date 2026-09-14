using System.Globalization;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class MaintenanceRecordService(
    DatabaseContextFactory contextFactory,
    AnnualMaintenanceDatabaseManager annualDatabases) : IMaintenanceRecordService
{
    private const int MaximumPageSize = 200;
    private const int MaximumSuggestionsPerField = 500;
    private const int SuggestionSampleLimit = 1_000;
    private MaintenanceEntrySuggestionCatalog? _suggestionCache;
    private long _suggestionGeneration;

    public Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve the proven year-selection behavior: discover real annual
        // partitions from Data/Years, but always expose the current year even on a
        // brand-new/temporarily empty data folder. Keep the list newest-first.
        var years = new SortedSet<int>(
            annualDatabases.DiscoverExistingYears(),
            Comparer<int>.Create((left, right) => right.CompareTo(left)))
        {
            DateTime.Today.Year
        };

        return Task.FromResult<IReadOnlyList<int>>([.. years]);
    }

    public async Task<MaintenancePage> GetPageAsync(MaintenanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        if (query.Year.HasValue) DataPaths.ValidateYear(query.Year.Value);
        if (query.FromInclusive.HasValue && query.ToInclusive.HasValue && query.FromInclusive > query.ToInclusive)
            throw new ArgumentException("Maintenance date range is invalid.", nameof(query));

        var years = SelectRelevantYears(query).ToArray();
        if (years.Length == 0) return new MaintenancePage([], null, false);

        if (query.Year.HasValue)
        {
            if (!annualDatabases.Exists(query.Year.Value)) return new MaintenancePage([], null, false);
            return await QueryYearPageAsync(query.Year.Value, query with { PageSize = pageSize }, cancellationToken).ConfigureAwait(false);
        }

        // Cross-year paging is a bounded k-way merge. Every relevant annual partition
        // returns at most pageSize rows after the same global keyset cursor, then only
        // the newest requested window is exposed to WPF. No annual table is materialized.
        var candidates = new List<MaintenanceRecord>(Math.Min(years.Length * pageSize, 8192));
        var partitionHasMore = false;
        foreach (var year in years)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var yearPage = await QueryYearPageAsync(year, query with { Year = year, PageSize = pageSize }, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(yearPage.Items);
            partitionHasMore |= yearPage.HasMore;
        }

        var ordered = candidates
            .OrderByDescending(item => item.OccurredAtUtcTicks)
            .ThenByDescending(item => item.CreatedAtUtcTicks)
            .ThenByDescending(item => item.ReferenceYear)
            .ThenByDescending(item => item.ReferenceSequence)
            .Take(pageSize + 1)
            .ToArray();
        var items = ordered.Take(pageSize).ToArray();
        var hasMore = ordered.Length > pageSize || partitionHasMore;
        var last = items.LastOrDefault();
        var next = hasMore && last is not null
            ? new MaintenancePageCursor(last.OccurredAtUtcTicks, last.CreatedAtUtcTicks, last.ReferenceYear, last.ReferenceSequence)
            : null;
        return new MaintenancePage(items, next, hasMore);
    }


    public async Task<MaintenanceActivityPage> GetActivityPageAsync(
        MaintenanceActivityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Year.HasValue) DataPaths.ValidateYear(query.Year.Value);
        if (query.FromInclusive.HasValue && query.ToInclusive.HasValue && query.FromInclusive > query.ToInclusive)
            throw new ArgumentException("Maintenance activity date range is invalid.", nameof(query));

        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        IEnumerable<int> years = annualDatabases.DiscoverExistingYears();
        if (query.Year.HasValue) years = years.Where(year => year == query.Year.Value);
        if (query.FromInclusive.HasValue) years = years.Where(year => year >= query.FromInclusive.Value.Year);
        if (query.ToInclusive.HasValue) years = years.Where(year => year <= query.ToInclusive.Value.Year);
        var selectedYears = years.OrderByDescending(year => year).ToArray();
        if (selectedYears.Length == 0) return new MaintenanceActivityPage([], null, false);

        var candidates = new List<MaintenanceActivityEvent>(Math.Min(selectedYears.Length * pageSize, 8192));
        var partitionHasMore = false;
        foreach (var year in selectedYears)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var yearQuery = query with { Year = year, PageSize = pageSize };
            if (query.Cursor is not null)
            {
                var cursor = query.Cursor;
                if (year > cursor.ReferenceYear)
                {
                    var beforeCursor = new DateTimeOffset(cursor.OccurredAtUtcTicks, TimeSpan.Zero);
                    if (beforeCursor > DateTimeOffset.MinValue) beforeCursor = beforeCursor.AddTicks(-1);
                    yearQuery = yearQuery with { ToInclusive = MinDate(yearQuery.ToInclusive, beforeCursor), Cursor = null };
                }
                else if (year == cursor.ReferenceYear)
                {
                    yearQuery = yearQuery with { Cursor = cursor };
                }
                else
                {
                    var throughCursor = new DateTimeOffset(cursor.OccurredAtUtcTicks, TimeSpan.Zero);
                    yearQuery = yearQuery with { ToInclusive = MinDate(yearQuery.ToInclusive, throughCursor), Cursor = null };
                }
            }

            var page = await QueryYearActivityPageAsync(year, yearQuery, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(page.Items);
            partitionHasMore |= page.HasMore;
        }

        var ordered = candidates
            .OrderByDescending(item => item.Activity.OccurredAtUtcTicks)
            .ThenByDescending(item => item.Record.ReferenceYear)
            .ThenByDescending(item => item.Activity.MaintenanceActivityId)
            .Take(pageSize + 1)
            .ToArray();
        var items = ordered.Take(pageSize).ToArray();
        var hasMore = ordered.Length > pageSize || partitionHasMore;
        var last = items.LastOrDefault();
        MaintenanceActivityPageCursor? next = null;
        if (hasMore && last is not null)
        {
            var countAtGroup = items.Count(item => item.Activity.OccurredAtUtcTicks == last.Activity.OccurredAtUtcTicks
                && item.Record.ReferenceYear == last.Record.ReferenceYear);
            var inherited = query.Cursor is not null
                && query.Cursor.OccurredAtUtcTicks == last.Activity.OccurredAtUtcTicks
                && query.Cursor.ReferenceYear == last.Record.ReferenceYear
                    ? query.Cursor.OffsetAtTimestamp
                    : 0;
            next = new MaintenanceActivityPageCursor(
                last.Activity.OccurredAtUtcTicks,
                last.Record.ReferenceYear,
                inherited + countAtGroup);
        }

        return new MaintenanceActivityPage(items, next, hasMore);
    }

    public async Task<IReadOnlyList<MaintenanceRecord>> GetAllMatchingAsync(
        MaintenanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var results = new List<MaintenanceRecord>();
        MaintenancePageCursor? cursor = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetPageAsync(query with { PageSize = MaximumPageSize, Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            results.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null) break;
            if (page.NextCursor == cursor)
                throw new InvalidOperationException("Maintenance paging did not advance. The exhaustive query was stopped to prevent an infinite loop.");
            cursor = page.NextCursor;
        }

        return results;
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
            MaintenancePageCursor? cursor = null;
            var sampled = 0;

            while (sampled < SuggestionSampleLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await GetPageAsync(new MaintenanceQuery(
                    IncludeInvalid: false,
                    PageSize: Math.Min(MaximumPageSize, SuggestionSampleLimit - sampled),
                    Cursor: cursor), cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    AddSuggestion(sites, item.Site);
                    AddSuggestion(locations, item.Location);
                    AddSuggestion(subjects, item.Subject);
                }
                sampled += page.Items.Count;
                if (!page.HasMore || page.NextCursor is null) break;
                if (page.NextCursor == cursor) break;
                cursor = page.NextCursor;
            }

            var catalog = new MaintenanceEntrySuggestionCatalog(
                OrderSuggestions(sites), OrderSuggestions(locations), OrderSuggestions(subjects));
            if (generation == Interlocked.Read(ref _suggestionGeneration))
            {
                Volatile.Write(ref _suggestionCache, catalog);
                return catalog;
            }
        }
    }

    public async Task<MaintenanceRecord?> GetByNumberAsync(string maintenanceNumber, CancellationToken cancellationToken = default)
    {
        var normalized = Clean(maintenanceNumber).ToUpperInvariant();
        if (!MaintenanceReference.TryParse(normalized, out var year, out _)) return null;
        if (!annualDatabases.Exists(year)) return null;
        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        return await context.MaintenanceRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => EF.Functions.Collate(item.MaintenanceNumber, "NOCASE") == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MaintenanceActivity>> GetActivityAsync(string maintenanceNumber, CancellationToken cancellationToken = default)
    {
        var record = await GetByNumberAsync(maintenanceNumber, cancellationToken).ConfigureAwait(false);
        if (record is null) return [];
        await using var context = annualDatabases.OpenExisting(record.ReferenceYear);
        return await context.MaintenanceActivity.AsNoTracking()
            .Where(item => item.MaintenanceRecordId == record.MaintenanceRecordId)
            .OrderBy(item => item.OccurredAtUtcTicks)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MaintenanceRecord> CreateAsync(MaintenanceRecordDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
        var referenceYear = draft.OccurredAt.Year;
        DataPaths.ValidateYear(referenceYear);
        await annualDatabases.EnsureAsync(referenceYear, cancellationToken).ConfigureAwait(false);

        Asset? asset = null;
        WorkOrder? workOrder = null;
        await using (var master = contextFactory.CreateMasterDbContext())
        {
            if (draft.AssetId.HasValue)
            {
                asset = await master.Asset.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.AssetId == draft.AssetId.Value && !item.IsArchived, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The selected asset was not found.");
            }

            if (draft.GeneratedByWorkOrderId.HasValue)
            {
                workOrder = await master.WorkOrders.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.WorkOrderId == draft.GeneratedByWorkOrderId.Value, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The source Work Order was not found.");
            }
        }

        if (workOrder is not null)
        {
            await using var existingContext = annualDatabases.OpenExisting(referenceYear);
            var existing = await existingContext.MaintenanceRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GeneratedByWorkOrderId == workOrder.WorkOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null) return existing;
        }

        string maintenanceNumber;
        await using (var master = contextFactory.CreateMasterDbContext())
        await using (var sequenceTransaction = await master.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            maintenanceNumber = await ReferenceNumberAllocator.ReserveMaintenanceAsync(master, referenceYear, cancellationToken).ConfigureAwait(false);
            await sequenceTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var record = new MaintenanceRecord
        {
            MaintenanceRecordId = Guid.NewGuid(),
            MaintenanceNumber = maintenanceNumber,
            ReferenceYear = referenceYear,
            ReferenceSequence = MaintenanceReference.TryParse(maintenanceNumber, out _, out var reservedSequence)
                ? reservedSequence
                : throw new InvalidOperationException("Reserved Maintenance reference is invalid."),
            OccurredAt = draft.OccurredAt,
            OccurredAtUtcTicks = draft.OccurredAt.UtcTicks,
            Site = Clean(draft.Site),
            Location = Clean(draft.Location),
            Subject = Clean(draft.Subject),
            MaintenanceType = Clean(draft.MaintenanceType),
            WorkPerformed = Clean(draft.WorkPerformed),
            PerformedBy = Clean(draft.PerformedBy),
            Cost = draft.Cost,
            Notes = Clean(draft.Notes),
            AssetId = asset?.AssetId,
            AssetNumberSnapshot = asset?.AssetNumber ?? string.Empty,
            AssetNameSnapshot = asset?.AssetName ?? string.Empty,
            GeneratedByWorkOrderId = workOrder?.WorkOrderId,
            WorkOrderNumberSnapshot = workOrder?.WorkOrderNumber ?? Clean(draft.WorkOrderNumberSnapshot),
            CreatedAtUtc = now,
            CreatedAtUtcTicks = now.UtcTicks,
            CreatedBy = Environment.UserName,
            UpdatedAtUtc = now,
            Revision = 1,
            IsInvalid = false,
            InvalidReason = string.Empty
        };

        await using var annual = annualDatabases.OpenExisting(referenceYear);
        await using var transaction = await annual.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (record.GeneratedByWorkOrderId.HasValue)
        {
            var existing = await annual.MaintenanceRecords
                .SingleOrDefaultAsync(item => item.GeneratedByWorkOrderId == record.GeneratedByWorkOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
        }

        annual.MaintenanceRecords.Add(record);
        annual.MaintenanceActivity.Add(CreateActivity(record, MaintenanceActivityTypes.Created, string.Empty,
            $"Type={record.MaintenanceType}; Location={record.Location}"));
        await annual.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        return record;
    }


    public async Task<MaintenanceRecord> EnsureWorkOrderMaintenanceAsync(
        WorkOrderGeneratedMaintenanceDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.WorkOrderId == Guid.Empty) throw new ArgumentException("Work Order ID is required.", nameof(draft));
        if (draft.MaintenanceRecordId == Guid.Empty) throw new ArgumentException("Maintenance record ID is required.", nameof(draft));
        DataPaths.ValidateYear(draft.ReferenceYear);
        if (!MaintenanceReference.TryParse(draft.MaintenanceNumber, out var parsedYear, out var parsedSequence)
            || parsedYear != draft.ReferenceYear
            || parsedSequence != draft.ReferenceSequence)
        {
            throw new InvalidOperationException("The reserved Maintenance identity is inconsistent.");
        }
        if (draft.OccurredAt.Year != draft.ReferenceYear)
            throw new InvalidOperationException("Work Order completion must be stored in the annual database matching its permanent MNT year.");
        if (string.IsNullOrWhiteSpace(draft.Location)) throw new ArgumentException("Location is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Subject)) throw new ArgumentException("Maintenance subject is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.WorkPerformed)) throw new ArgumentException("Work performed is required.", nameof(draft));
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
        ExactNumericStorage.ValidateDowntimeHours(draft.DowntimeHours, nameof(draft));

        await annualDatabases.EnsureAsync(draft.ReferenceYear, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(draft.ReferenceYear);
        var existing = await context.MaintenanceRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GeneratedByWorkOrderId == draft.WorkOrderId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureReservedIdentity(existing, draft);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var record = new MaintenanceRecord
        {
            MaintenanceRecordId = draft.MaintenanceRecordId,
            MaintenanceNumber = Clean(draft.MaintenanceNumber).ToUpperInvariant(),
            ReferenceYear = draft.ReferenceYear,
            ReferenceSequence = draft.ReferenceSequence,
            OccurredAt = draft.OccurredAt,
            OccurredAtUtcTicks = draft.OccurredAt.UtcTicks,
            Site = Clean(draft.Site),
            Location = Clean(draft.Location),
            Subject = Clean(draft.Subject),
            MaintenanceType = Clean(draft.MaintenanceType),
            WorkPerformed = Clean(draft.WorkPerformed),
            PerformedBy = Clean(draft.PerformedBy),
            Cost = draft.Cost,
            DowntimeHours = draft.DowntimeHours,
            Notes = Clean(draft.Notes),
            AssetId = draft.AssetId,
            AssetNumberSnapshot = Clean(draft.AssetNumberSnapshot),
            AssetNameSnapshot = Clean(draft.AssetNameSnapshot),
            GeneratedByWorkOrderId = draft.WorkOrderId,
            WorkOrderNumberSnapshot = Clean(draft.WorkOrderNumberSnapshot),
            CreatedAtUtc = now,
            CreatedAtUtcTicks = now.UtcTicks,
            CreatedBy = Environment.UserName,
            UpdatedAtUtc = now,
            Revision = 1
        };

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        context.MaintenanceRecords.Add(record);
        context.MaintenanceActivity.Add(CreateActivity(record, MaintenanceActivityTypes.Created, string.Empty,
            $"SourceWorkOrder={record.WorkOrderNumberSnapshot}; Type={record.MaintenanceType}; Location={record.Location}"));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            InvalidateSuggestionCache();
            return record;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var raced = await context.MaintenanceRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GeneratedByWorkOrderId == draft.WorkOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (raced is null) throw;
            EnsureReservedIdentity(raced, draft);
            return raced;
        }
    }

    public async Task<MaintenanceAggregate> GetAggregateAsync(MaintenanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Year.HasValue) DataPaths.ValidateYear(query.Year.Value);
        if (query.FromInclusive.HasValue && query.ToInclusive.HasValue && query.FromInclusive > query.ToInclusive)
            throw new ArgumentException("Maintenance date range is invalid.", nameof(query));

        long count = 0;
        decimal total = 0m;
        foreach (var year in SelectRelevantYears(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!annualDatabases.Exists(year)) continue;
            var aggregate = await QueryYearAggregateAsync(year, query with { Year = year, Cursor = null }, cancellationToken).ConfigureAwait(false);
            count += aggregate.Count;
            total += aggregate.TotalCost;
        }
        return new MaintenanceAggregate(count, total);
    }

    public async Task<MaintenanceDashboardSummary> GetDashboardSummaryAsync(int year, CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        if (!annualDatabases.Exists(year))
            return new MaintenanceDashboardSummary(year, 0, 0m, [], [], []);

        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var monthStartLocal = new DateTimeOffset(new DateTime(year, now.Month, 1, 0, 0, 0, DateTimeKind.Local));
        var monthEndLocal = monthStartLocal.AddMonths(1).AddTicks(-1);
        var month = await QueryYearAggregateAsync(year, new MaintenanceQuery(
            Year: year, FromInclusive: monthStartLocal, ToInclusive: monthEndLocal, IncludeInvalid: false), cancellationToken).ConfigureAwait(false);
        var recent = await QueryYearPageAsync(year, new MaintenanceQuery(Year: year, IncludeInvalid: false, PageSize: 6), cancellationToken).ConfigureAwait(false);
        var topLocations = await QuerySummaryMetricsAsync(year, "MaintenanceLocationSummary", cancellationToken).ConfigureAwait(false);
        var topTypes = await QuerySummaryMetricsAsync(year, "MaintenanceTypeSummary", cancellationToken).ConfigureAwait(false);
        return new MaintenanceDashboardSummary(year, month.Count, month.TotalCost, recent.Items, topLocations, topTypes);
    }

    public async Task<MaintenanceDashboardSummaryVerification> VerifyDashboardSummariesAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        if (!annualDatabases.Exists(year))
            return new MaintenanceDashboardSummaryVerification(year, true, 0, 0);

        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var locationMismatchCount = await QuerySummaryMismatchCountAsync(
            connection, "MaintenanceLocationSummary", "Location", cancellationToken).ConfigureAwait(false);
        var typeMismatchCount = await QuerySummaryMismatchCountAsync(
            connection, "MaintenanceTypeSummary", "MaintenanceType", cancellationToken).ConfigureAwait(false);

        return new MaintenanceDashboardSummaryVerification(
            year,
            locationMismatchCount == 0 && typeMismatchCount == 0,
            locationMismatchCount,
            typeMismatchCount);
    }

    public async Task RebuildDashboardSummariesAsync(int year, CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM MaintenanceLocationSummary;
            INSERT INTO MaintenanceLocationSummary (Label, RecordCount, TotalCostMinorUnits)
            SELECT Location, COUNT(*), COALESCE(SUM(CostMinorUnits), 0)
            FROM MaintenanceRecords
            WHERE IsInvalid = 0 AND Location <> ''
            GROUP BY Location;

            DELETE FROM MaintenanceTypeSummary;
            INSERT INTO MaintenanceTypeSummary (Label, RecordCount, TotalCostMinorUnits)
            SELECT MaintenanceType, COUNT(*), COALESCE(SUM(CostMinorUnits), 0)
            FROM MaintenanceRecords
            WHERE IsInvalid = 0 AND MaintenanceType <> ''
            GROUP BY MaintenanceType;
            """,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MaintenanceRecord> UpdateAsync(
        Guid maintenanceRecordId,
        int referenceYear,
        MaintenanceRecordEditDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (maintenanceRecordId == Guid.Empty) throw new ArgumentException("Maintenance record ID is required.", nameof(maintenanceRecordId));
        DataPaths.ValidateYear(referenceYear);
        Validate(draft);
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
        if (draft.OccurredAt.Year != referenceYear)
            throw new InvalidOperationException("A Maintenance correction cannot move a permanent MNT into a different annual database. Mark the incorrect record invalid and create a corrected MNT in the proper year.");
        if (!annualDatabases.Exists(referenceYear)) throw new InvalidOperationException("Maintenance record was not found.");
        await annualDatabases.EnsureAsync(referenceYear, cancellationToken).ConfigureAwait(false);

        await using var context = annualDatabases.OpenExisting(referenceYear);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await context.MaintenanceRecords
            .SingleOrDefaultAsync(item => item.MaintenanceRecordId == maintenanceRecordId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Maintenance record was not found.");
        if (record.IsInvalid) throw new InvalidOperationException("An invalid Maintenance record cannot be edited.");

        var changes = BuildChanges(record, draft);
        if (changes.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }

        record.OccurredAt = draft.OccurredAt;
        record.OccurredAtUtcTicks = draft.OccurredAt.UtcTicks;
        record.Site = Clean(draft.Site);
        record.Location = Clean(draft.Location);
        record.Subject = Clean(draft.Subject);
        record.MaintenanceType = Clean(draft.MaintenanceType);
        record.WorkPerformed = Clean(draft.WorkPerformed);
        record.PerformedBy = Clean(draft.PerformedBy);
        record.Cost = draft.Cost;
        record.Notes = Clean(draft.Notes);
        record.Revision = Math.Max(record.Revision, 1) + 1;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        context.MaintenanceActivity.Add(CreateActivity(record, MaintenanceActivityTypes.Edited, "Correction", string.Join(" | ", changes)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        return record;
    }

    public async Task<MaintenanceRecord> MarkInvalidAsync(
        Guid maintenanceRecordId,
        int referenceYear,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (maintenanceRecordId == Guid.Empty) throw new ArgumentException("Maintenance record ID is required.", nameof(maintenanceRecordId));
        DataPaths.ValidateYear(referenceYear);
        var normalizedReason = Clean(reason);
        if (normalizedReason.Length == 0) throw new ArgumentException("A reason is required before marking Maintenance invalid.", nameof(reason));
        if (!annualDatabases.Exists(referenceYear)) throw new InvalidOperationException("Maintenance record was not found.");
        await annualDatabases.EnsureAsync(referenceYear, cancellationToken).ConfigureAwait(false);

        await using var context = annualDatabases.OpenExisting(referenceYear);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await context.MaintenanceRecords
            .SingleOrDefaultAsync(item => item.MaintenanceRecordId == maintenanceRecordId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Maintenance record was not found.");
        if (record.IsInvalid) return record;

        record.IsInvalid = true;
        record.InvalidReason = normalizedReason;
        record.Revision = Math.Max(record.Revision, 1) + 1;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        context.MaintenanceActivity.Add(CreateActivity(record, MaintenanceActivityTypes.MarkedInvalid, normalizedReason, "IsInvalid=true"));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateSuggestionCache();
        return record;
    }

    private void InvalidateSuggestionCache()
    {
        Interlocked.Increment(ref _suggestionGeneration);
        Volatile.Write(ref _suggestionCache, null);
    }


    private async Task<MaintenanceActivityPage> QueryYearActivityPageAsync(
        int year,
        MaintenanceActivityQuery query,
        CancellationToken cancellationToken)
    {
        if (!annualDatabases.Exists(year)) return new MaintenanceActivityPage([], null, false);
        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);

        var rows = context.MaintenanceActivity.AsNoTracking()
            .Join(
                context.MaintenanceRecords.AsNoTracking(),
                activity => activity.MaintenanceRecordId,
                record => record.MaintenanceRecordId,
                (activity, record) => new { Activity = activity, Record = record });

        if (query.FromInclusive.HasValue)
        {
            var ticks = query.FromInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.Activity.OccurredAtUtcTicks >= ticks);
        }
        if (query.ToInclusive.HasValue)
        {
            var ticks = query.ToInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.Activity.OccurredAtUtcTicks <= ticks);
        }
        if (!string.IsNullOrWhiteSpace(query.ActivityType))
        {
            var value = query.ActivityType.Trim();
            rows = rows.Where(item => item.Activity.ActivityType == value);
        }
        if (!string.IsNullOrWhiteSpace(query.Site))
        {
            var value = query.Site.Trim();
            rows = rows.Where(item => item.Record.Site == value);
        }
        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            var value = query.Location.Trim();
            rows = rows.Where(item => item.Record.Location == value);
        }
        if (!string.IsNullOrWhiteSpace(query.MaintenanceType))
        {
            var value = query.MaintenanceType.Trim();
            rows = rows.Where(item => item.Record.MaintenanceType == value);
        }
        if (!string.IsNullOrWhiteSpace(query.PerformedBy))
        {
            var value = query.PerformedBy.Trim();
            rows = rows.Where(item => item.Record.PerformedBy == value || item.Activity.ChangedBy == value);
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var value = query.SearchText.Trim();
            rows = rows.Where(item => item.Record.MaintenanceNumber.Contains(value)
                || item.Record.Subject.Contains(value)
                || item.Record.WorkPerformed.Contains(value)
                || item.Record.Site.Contains(value)
                || item.Record.Location.Contains(value)
                || item.Record.PerformedBy.Contains(value)
                || item.Record.Notes.Contains(value)
                || item.Activity.ActivityType.Contains(value)
                || item.Activity.Reason.Contains(value)
                || item.Activity.Changes.Contains(value)
                || item.Activity.ChangedBy.Contains(value));
        }

        var skipAtTimestamp = 0;
        if (query.Cursor is not null && query.Cursor.ReferenceYear == year)
        {
            var cursorTicks = query.Cursor.OccurredAtUtcTicks;
            rows = rows.Where(item => item.Activity.OccurredAtUtcTicks <= cursorTicks);
            skipAtTimestamp = Math.Max(query.Cursor.OffsetAtTimestamp, 0);
        }

        var take = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var materialized = await rows
            .OrderByDescending(item => item.Activity.OccurredAtUtcTicks)
            .ThenByDescending(item => item.Activity.MaintenanceActivityId)
            .Take(take + skipAtTimestamp + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var remaining = materialized.AsEnumerable();
        if (skipAtTimestamp > 0) remaining = remaining.Skip(skipAtTimestamp);
        var window = remaining.Take(take + 1).ToArray();
        var hasMore = window.Length > take;
        var items = window.Take(take)
            .Select(item => new MaintenanceActivityEvent(item.Record, item.Activity))
            .ToArray();
        var last = items.LastOrDefault();
        MaintenanceActivityPageCursor? next = null;
        if (hasMore && last is not null)
        {
            var countAtTimestamp = items.Count(item => item.Activity.OccurredAtUtcTicks == last.Activity.OccurredAtUtcTicks);
            var inherited = query.Cursor is not null && query.Cursor.ReferenceYear == year
                && query.Cursor.OccurredAtUtcTicks == last.Activity.OccurredAtUtcTicks
                    ? query.Cursor.OffsetAtTimestamp
                    : 0;
            next = new MaintenanceActivityPageCursor(last.Activity.OccurredAtUtcTicks, year, inherited + countAtTimestamp);
        }
        return new MaintenanceActivityPage(items, next, hasMore);
    }

    private static DateTimeOffset? MinDate(DateTimeOffset? current, DateTimeOffset candidate) =>
        !current.HasValue || candidate < current.Value ? candidate : current;

    private IEnumerable<int> SelectRelevantYears(MaintenanceQuery query)
    {
        IEnumerable<int> years = annualDatabases.DiscoverExistingYears();
        if (query.Year.HasValue) years = years.Where(year => year == query.Year.Value);
        if (query.FromInclusive.HasValue) years = years.Where(year => year >= query.FromInclusive.Value.Year);
        if (query.ToInclusive.HasValue) years = years.Where(year => year <= query.ToInclusive.Value.Year);
        return years.OrderByDescending(year => year);
    }

    private async Task<MaintenancePage> QueryYearPageAsync(int year, MaintenanceQuery query, CancellationToken cancellationToken)
    {
        if (!ShouldSplitInvalidBranches(query))
        {
            return await QueryYearPageCoreAsync(
                year,
                query,
                query.IncludeInvalid ? null : false,
                cancellationToken).ConfigureAwait(false);
        }

        // Existing composite indexes place IsInvalid between the filter key and the
        // temporal ordering columns. When callers intentionally include both valid and
        // invalid rows, query each equality branch independently so SQLite can retain
        // the complete index prefix, then merge the two bounded windows in memory.
        // This avoids adding large duplicate indexes to every annual database.
        var valid = await QueryYearPageCoreAsync(year, query, false, cancellationToken).ConfigureAwait(false);
        var invalid = await QueryYearPageCoreAsync(year, query, true, cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var merged = valid.Items.Concat(invalid.Items)
            .OrderByDescending(item => item.OccurredAtUtcTicks)
            .ThenByDescending(item => item.CreatedAtUtcTicks)
            .ThenByDescending(item => item.ReferenceSequence)
            .Take(take + 1)
            .ToArray();
        var items = merged.Take(take).ToArray();
        var hasMore = merged.Length > take || valid.HasMore || invalid.HasMore;
        var last = items.LastOrDefault();
        var next = hasMore && last is not null
            ? new MaintenancePageCursor(last.OccurredAtUtcTicks, last.CreatedAtUtcTicks, last.ReferenceYear, last.ReferenceSequence)
            : null;
        return new MaintenancePage(items, next, hasMore);
    }

    private async Task<MaintenancePage> QueryYearPageCoreAsync(
        int year,
        MaintenanceQuery query,
        bool? forcedInvalid,
        CancellationToken cancellationToken)
    {
        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        IQueryable<MaintenanceRecord> rows = forcedInvalid switch
        {
            false => context.MaintenanceRecords
                .FromSqlRaw("SELECT * FROM MaintenanceRecords WHERE IsInvalid = 0")
                .AsNoTracking(),
            true => context.MaintenanceRecords
                .FromSqlRaw("SELECT * FROM MaintenanceRecords WHERE IsInvalid = 1")
                .AsNoTracking(),
            _ => context.MaintenanceRecords.AsNoTracking()
        };

        rows = ApplyStructuredFilters(rows, query);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var value = query.SearchText.Trim();
            if (MaintenanceReference.TryParse(value.ToUpperInvariant(), out var searchYear, out _))
            {
                if (searchYear != year) return new MaintenancePage([], null, false);
                var normalized = value.ToUpperInvariant();
                rows = rows.Where(item => item.MaintenanceNumber == normalized);
            }
            else
            {
                var ftsQuery = BuildFtsQuery(value);
                rows = forcedInvalid switch
                {
                    false => context.MaintenanceRecords.FromSqlInterpolated($$"""
                        SELECT mr.*
                        FROM MaintenanceRecords AS mr
                        INNER JOIN MaintenanceSearch ON MaintenanceSearch.MaintenanceRecordId = mr.MaintenanceRecordId
                        WHERE MaintenanceSearch MATCH {{ftsQuery}}
                          AND mr.IsInvalid = 0
                        """).AsNoTracking(),
                    true => context.MaintenanceRecords.FromSqlInterpolated($$"""
                        SELECT mr.*
                        FROM MaintenanceRecords AS mr
                        INNER JOIN MaintenanceSearch ON MaintenanceSearch.MaintenanceRecordId = mr.MaintenanceRecordId
                        WHERE MaintenanceSearch MATCH {{ftsQuery}}
                          AND mr.IsInvalid = 1
                        """).AsNoTracking(),
                    _ => context.MaintenanceRecords.FromSqlInterpolated($$"""
                        SELECT mr.*
                        FROM MaintenanceRecords AS mr
                        INNER JOIN MaintenanceSearch ON MaintenanceSearch.MaintenanceRecordId = mr.MaintenanceRecordId
                        WHERE MaintenanceSearch MATCH {{ftsQuery}}
                        """).AsNoTracking()
                };
                rows = ApplyStructuredFilters(rows, query);
            }
        }

        if (query.Cursor is not null)
        {
            var cursor = query.Cursor;
            rows = rows.Where(item =>
                item.OccurredAtUtcTicks < cursor.OccurredAtUtcTicks
                || (item.OccurredAtUtcTicks == cursor.OccurredAtUtcTicks && item.CreatedAtUtcTicks < cursor.CreatedAtUtcTicks)
                || (item.OccurredAtUtcTicks == cursor.OccurredAtUtcTicks
                    && item.CreatedAtUtcTicks == cursor.CreatedAtUtcTicks
                    && (year < cursor.ReferenceYear
                        || (year == cursor.ReferenceYear && item.ReferenceSequence < cursor.ReferenceSequence))));
        }

        var take = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var materialized = await rows
            .OrderByDescending(item => item.OccurredAtUtcTicks)
            .ThenByDescending(item => item.CreatedAtUtcTicks)
            .ThenByDescending(item => item.ReferenceSequence)
            .Take(take + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = materialized.Length > take;
        var items = materialized.Take(take).ToArray();
        var last = items.LastOrDefault();
        var cursorResult = hasMore && last is not null
            ? new MaintenancePageCursor(last.OccurredAtUtcTicks, last.CreatedAtUtcTicks, last.ReferenceYear, last.ReferenceSequence)
            : null;
        return new MaintenancePage(items, cursorResult, hasMore);
    }

    private static bool ShouldSplitInvalidBranches(MaintenanceQuery query) =>
        query.IncludeInvalid
        && string.IsNullOrWhiteSpace(query.SearchText)
        && (query.AssetId.HasValue
            || !string.IsNullOrWhiteSpace(query.Site)
            || !string.IsNullOrWhiteSpace(query.Location)
            || !string.IsNullOrWhiteSpace(query.MaintenanceType)
            || !string.IsNullOrWhiteSpace(query.PerformedBy));


    private async Task<MaintenanceAggregate> QueryYearAggregateAsync(int year, MaintenanceQuery query, CancellationToken cancellationToken)
    {
        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = BuildSqlWhere(command, query);
        command.CommandText = $"SELECT COUNT(*), COALESCE(SUM(CostMinorUnits), 0) FROM MaintenanceRecords {where};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new MaintenanceAggregate(0, 0m);
        var count = reader.GetInt64(0);
        var totalMinorUnits = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        return new MaintenanceAggregate(count, totalMinorUnits / ExactNumericStorage.MoneyScale);
    }

    private async Task<IReadOnlyList<MaintenanceMetric>> QuerySummaryMetricsAsync(int year, string summaryTable, CancellationToken cancellationToken)
    {
        if (summaryTable is not "MaintenanceLocationSummary" and not "MaintenanceTypeSummary")
            throw new ArgumentOutOfRangeException(nameof(summaryTable));

        await annualDatabases.EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        await using var context = annualDatabases.OpenExisting(year);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Label, RecordCount, TotalCostMinorUnits FROM {summaryTable} WHERE RecordCount > 0 ORDER BY RecordCount DESC, Label COLLATE NOCASE LIMIT 5;";
        var result = new List<MaintenanceMetric>(5);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MaintenanceMetric(
                reader.GetString(0),
                reader.GetInt64(1),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) / ExactNumericStorage.MoneyScale));
        }
        return result;
    }

    private static async Task<long> QuerySummaryMismatchCountAsync(
        System.Data.Common.DbConnection connection,
        string summaryTable,
        string sourceColumn,
        CancellationToken cancellationToken)
    {
        var validPair = (string.Equals(summaryTable, "MaintenanceLocationSummary", StringComparison.Ordinal)
                         && string.Equals(sourceColumn, "Location", StringComparison.Ordinal))
                        || (string.Equals(summaryTable, "MaintenanceTypeSummary", StringComparison.Ordinal)
                            && string.Equals(sourceColumn, "MaintenanceType", StringComparison.Ordinal));
        if (!validPair) throw new ArgumentOutOfRangeException(nameof(summaryTable));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH Canonical AS (
                SELECT {sourceColumn} AS Label,
                       COUNT(*) AS RecordCount,
                       COALESCE(SUM(CostMinorUnits), 0) AS TotalCostMinorUnits
                FROM MaintenanceRecords
                WHERE IsInvalid = 0 AND {sourceColumn} <> ''
                GROUP BY {sourceColumn}
            ),
            Mismatches AS (
                SELECT Canonical.Label
                FROM Canonical
                LEFT JOIN {summaryTable} AS Summary ON Summary.Label = Canonical.Label
                WHERE Summary.Label IS NULL
                   OR Summary.RecordCount <> Canonical.RecordCount
                   OR Summary.TotalCostMinorUnits <> Canonical.TotalCostMinorUnits
                UNION ALL
                SELECT Summary.Label
                FROM {summaryTable} AS Summary
                LEFT JOIN Canonical ON Canonical.Label = Summary.Label
                WHERE Canonical.Label IS NULL OR Summary.RecordCount <= 0
            )
            SELECT COUNT(*) FROM Mismatches;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string BuildSqlWhere(System.Data.Common.DbCommand command, MaintenanceQuery query)
    {
        var clauses = new List<string>();
        void Add(string clause) => clauses.Add(clause);
        void Parameter(string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        if (!query.IncludeInvalid) Add("IsInvalid = 0");
        if (query.AssetId.HasValue) { Add("AssetId = $assetId"); Parameter("$assetId", query.AssetId.Value); }
        if (query.FromInclusive.HasValue) { Add("OccurredAtUtcTicks >= $fromTicks"); Parameter("$fromTicks", query.FromInclusive.Value.UtcTicks); }
        if (query.ToInclusive.HasValue) { Add("OccurredAtUtcTicks <= $toTicks"); Parameter("$toTicks", query.ToInclusive.Value.UtcTicks); }
        if (!string.IsNullOrWhiteSpace(query.Site)) { Add("Site = $site"); Parameter("$site", query.Site.Trim()); }
        if (!string.IsNullOrWhiteSpace(query.Location)) { Add("Location = $location"); Parameter("$location", query.Location.Trim()); }
        if (!string.IsNullOrWhiteSpace(query.MaintenanceType)) { Add("MaintenanceType = $type"); Parameter("$type", query.MaintenanceType.Trim()); }
        if (!string.IsNullOrWhiteSpace(query.PerformedBy)) { Add("PerformedBy = $performedBy"); Parameter("$performedBy", query.PerformedBy.Trim()); }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var value = query.SearchText.Trim();
            if (MaintenanceReference.TryParse(value.ToUpperInvariant(), out _, out _))
            {
                Add("MaintenanceNumber = $maintenanceNumber COLLATE NOCASE");
                Parameter("$maintenanceNumber", value.ToUpperInvariant());
            }
            else
            {
                Add("MaintenanceRecordId IN (SELECT MaintenanceRecordId FROM MaintenanceSearch WHERE MaintenanceSearch MATCH $fts)");
                Parameter("$fts", BuildFtsQuery(value));
            }
        }
        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static IQueryable<MaintenanceRecord> ApplyStructuredFilters(IQueryable<MaintenanceRecord> rows, MaintenanceQuery query)
    {
        if (query.AssetId.HasValue) rows = rows.Where(item => item.AssetId == query.AssetId.Value);
        if (query.FromInclusive.HasValue)
        {
            var fromTicks = query.FromInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.OccurredAtUtcTicks >= fromTicks);
        }
        if (query.ToInclusive.HasValue)
        {
            var toTicks = query.ToInclusive.Value.UtcTicks;
            rows = rows.Where(item => item.OccurredAtUtcTicks <= toTicks);
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
        if (!string.IsNullOrWhiteSpace(query.MaintenanceType))
        {
            var value = query.MaintenanceType.Trim();
            rows = rows.Where(item => item.MaintenanceType == value);
        }
        if (!string.IsNullOrWhiteSpace(query.PerformedBy))
        {
            var value = query.PerformedBy.Trim();
            rows = rows.Where(item => item.PerformedBy == value);
        }
        return rows;
    }

    private static void AddSuggestion(HashSet<string> values, string? value)
    {
        if (values.Count >= MaximumSuggestionsPerField) return;
        var cleaned = Clean(value);
        if (cleaned.Length > 0) values.Add(cleaned);
    }

    private static string[] OrderSuggestions(IEnumerable<string> values) =>
        values.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private static string BuildFtsQuery(string searchText)
    {
        var terms = searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return string.Empty;
        return string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\"*"));
    }

    private static void EnsureReservedIdentity(MaintenanceRecord existing, WorkOrderGeneratedMaintenanceDraft draft)
    {
        if (existing.MaintenanceRecordId != draft.MaintenanceRecordId
            || !string.Equals(existing.MaintenanceNumber, draft.MaintenanceNumber, StringComparison.OrdinalIgnoreCase)
            || existing.ReferenceYear != draft.ReferenceYear
            || existing.ReferenceSequence != draft.ReferenceSequence)
        {
            throw new InvalidOperationException("A Work Order is already linked to a different permanent Maintenance identity. Recovery stopped to protect history.");
        }
    }

    private static MaintenanceActivity CreateActivity(MaintenanceRecord record, string type, string reason, string changes)
    {
        var now = DateTimeOffset.UtcNow;
        return new MaintenanceActivity
        {
            MaintenanceActivityId = Guid.NewGuid(),
            MaintenanceRecordId = record.MaintenanceRecordId,
            OccurredAtUtc = now,
            OccurredAtUtcTicks = now.UtcTicks,
            ActivityType = type,
            Revision = record.Revision,
            Reason = Clean(reason),
            Changes = Clean(changes),
            ChangedBy = Environment.UserName
        };
    }

    private static void Validate(MaintenanceRecordDraft draft)
    {
        if (draft.OccurredAt == default) throw new ArgumentException("Maintenance date is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Location)) throw new ArgumentException("Location is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Subject)) throw new ArgumentException("Maintenance subject is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.MaintenanceType)) throw new ArgumentException("Maintenance type is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.WorkPerformed)) throw new ArgumentException("Work performed is required.", nameof(draft));
        if (draft.AssetId == Guid.Empty) throw new ArgumentException("Asset ID is invalid.", nameof(draft));
        if (draft.GeneratedByWorkOrderId == Guid.Empty) throw new ArgumentException("Work Order ID is invalid.", nameof(draft));
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
    }

    private static void Validate(MaintenanceRecordEditDraft draft)
    {
        if (draft.OccurredAt == default) throw new ArgumentException("Maintenance date is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Location)) throw new ArgumentException("Location is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Subject)) throw new ArgumentException("Maintenance subject is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.MaintenanceType)) throw new ArgumentException("Maintenance type is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.WorkPerformed)) throw new ArgumentException("Work performed is required.", nameof(draft));
        ExactNumericStorage.ValidateMoney(draft.Cost, nameof(draft));
    }

    private static List<string> BuildChanges(MaintenanceRecord record, MaintenanceRecordEditDraft draft)
    {
        var changes = new List<string>();
        AddChange(changes, "Date", record.OccurredAt, draft.OccurredAt);
        AddChange(changes, "Site", record.Site, Clean(draft.Site));
        AddChange(changes, "Location", record.Location, Clean(draft.Location));
        AddChange(changes, "Subject", record.Subject, Clean(draft.Subject));
        AddChange(changes, "MaintenanceType", record.MaintenanceType, Clean(draft.MaintenanceType));
        AddChange(changes, "WorkPerformed", record.WorkPerformed, Clean(draft.WorkPerformed));
        AddChange(changes, "PerformedBy", record.PerformedBy, Clean(draft.PerformedBy));
        AddChange(changes, "Cost", record.Cost, draft.Cost);
        AddChange(changes, "Notes", record.Notes, Clean(draft.Notes));
        return changes;
    }

    private static void AddChange<T>(List<string> changes, string name, T before, T after)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        changes.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: '{before}' -> '{after}'"));
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
