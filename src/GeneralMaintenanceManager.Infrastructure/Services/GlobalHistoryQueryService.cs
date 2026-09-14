using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Infrastructure.Services;

/// <summary>
/// Bounded read-model service for the Maintenance and Reports workspaces. It deliberately
/// composes page-sized source windows and never calls any exhaustive history helper.
/// </summary>
public sealed class GlobalHistoryQueryService(
    IMaintenanceRecordService maintenanceRecords,
    IActivityHistoryService activityHistory) : IGlobalHistoryQueryService
{
    private const int MaximumPageSize = 200;

    public async Task<GlobalHistoryPage> GetPageAsync(GlobalHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Year.HasValue) DataPaths.ValidateYear(query.Year.Value);
        if (query.FromInclusive.HasValue && query.ToInclusive.HasValue && query.FromInclusive > query.ToInclusive)
            throw new ArgumentException("Global history date range is invalid.", nameof(query));

        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        return query.Mode switch
        {
            GlobalHistoryModes.Maintenance => await GetMaintenancePageAsync(query with { PageSize = pageSize }, cancellationToken).ConfigureAwait(false),
            GlobalHistoryModes.WorkOrders => await GetMasterActivityPageAsync(query with { PageSize = pageSize }, ActivityRecordTypes.WorkOrder, cancellationToken).ConfigureAwait(false),
            GlobalHistoryModes.PreventiveMaintenance => await GetMasterActivityPageAsync(query with { PageSize = pageSize }, ActivityRecordTypes.PreventiveMaintenance, cancellationToken).ConfigureAwait(false),
            GlobalHistoryModes.AllActivity => await GetAllActivityPageAsync(query with { PageSize = pageSize }, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(query), $"Unknown history mode '{query.Mode}'.")
        };
    }

    private async Task<GlobalHistoryPage> GetMaintenancePageAsync(GlobalHistoryQuery query, CancellationToken cancellationToken)
    {
        var page = await maintenanceRecords.GetPageAsync(new MaintenanceQuery(
            Year: query.Year,
            FromInclusive: query.FromInclusive,
            ToInclusive: query.ToInclusive,
            Site: query.Site,
            Location: query.Location,
            MaintenanceType: query.MaintenanceType,
            PerformedBy: query.PerformedBy,
            SearchText: query.SearchText,
            IncludeInvalid: query.IncludeInvalid,
            PageSize: query.PageSize,
            Cursor: query.Cursor?.MaintenanceCursor), cancellationToken).ConfigureAwait(false);

        var rows = page.Items.Select(item => new GlobalHistoryRow(item, null, item.ReferenceYear)).ToArray();
        var next = page.HasMore && page.NextCursor is not null
            ? new GlobalHistoryPageCursor(MaintenanceCursor: page.NextCursor)
            : null;
        return new GlobalHistoryPage(rows, next, page.HasMore);
    }

    private async Task<GlobalHistoryPage> GetMasterActivityPageAsync(
        GlobalHistoryQuery query,
        string recordType,
        CancellationToken cancellationToken)
    {
        var page = await activityHistory.GetPageAsync(new ActivityHistoryQuery(
            Year: query.Year,
            FromInclusive: query.FromInclusive,
            ToInclusive: query.ToInclusive,
            RecordType: recordType,
            ActivityType: query.ActivityType,
            Site: query.Site,
            Location: query.Location,
            ChangedBy: query.PerformedBy,
            SearchText: query.SearchText,
            PageSize: query.PageSize,
            Cursor: query.Cursor?.ActivityCursor), cancellationToken).ConfigureAwait(false);

        var rows = page.Items.Select(item => new GlobalHistoryRow(null, item, item.OccurredAtUtc.LocalDateTime.Year, GlobalHistorySource.MasterActivity)).ToArray();
        var next = page.HasMore && page.NextCursor is not null
            ? new GlobalHistoryPageCursor(ActivityCursor: page.NextCursor)
            : null;
        return new GlobalHistoryPage(rows, next, page.HasMore);
    }

    private async Task<GlobalHistoryPage> GetAllActivityPageAsync(GlobalHistoryQuery query, CancellationToken cancellationToken)
    {
        var cursor = query.Cursor;
        DateTimeOffset? masterTo = query.ToInclusive;
        DateTimeOffset? maintenanceTo = query.ToInclusive;
        ActivityHistoryPageCursor? masterCursor = null;
        MaintenanceActivityPageCursor? maintenanceCursor = null;

        if (cursor?.AllActivityOccurredAtUtcTicks is long cursorTicks && cursor.AllActivitySource.HasValue)
        {
            var cursorTime = new DateTimeOffset(cursorTicks, TimeSpan.Zero);
            if (cursor.AllActivitySource == GlobalHistorySource.MasterActivity)
            {
                masterCursor = new ActivityHistoryPageCursor(cursorTicks, cursor.AllActivityOffsetAtTimestamp);
                maintenanceTo = MinDate(maintenanceTo, cursorTime);
            }
            else
            {
                masterTo = MinDate(masterTo, SafePreviousTick(cursorTime));
                maintenanceCursor = new MaintenanceActivityPageCursor(
                    cursorTicks,
                    cursor.AllActivityReferenceYear,
                    cursor.AllActivityOffsetAtTimestamp);
            }
        }

        Task<ActivityHistoryPage> masterTask;
        if (!string.IsNullOrWhiteSpace(query.MaintenanceType))
        {
            // Maintenance type is an annual Maintenance-record field. Master Work Order /
            // Preventive activity rows do not carry it, matching the previous report semantics.
            masterTask = Task.FromResult(new ActivityHistoryPage([], null, false));
        }
        else
        {
            masterTask = activityHistory.GetPageAsync(new ActivityHistoryQuery(
                Year: query.Year,
                FromInclusive: query.FromInclusive,
                ToInclusive: masterTo,
                ActivityType: query.ActivityType,
                Site: query.Site,
                Location: query.Location,
                ChangedBy: query.PerformedBy,
                SearchText: query.SearchText,
                PageSize: query.PageSize,
                Cursor: masterCursor), cancellationToken);
        }

        var maintenanceActivityType = MapMaintenanceActivityFilter(query.ActivityType);
        Task<MaintenanceActivityPage> maintenanceTask;
        if (maintenanceActivityType is null)
        {
            maintenanceTask = Task.FromResult(new MaintenanceActivityPage([], null, false));
        }
        else
        {
            maintenanceTask = maintenanceRecords.GetActivityPageAsync(new MaintenanceActivityQuery(
                Year: query.Year,
                FromInclusive: query.FromInclusive,
                ToInclusive: maintenanceTo,
                ActivityType: maintenanceActivityType,
                Site: query.Site,
                Location: query.Location,
                MaintenanceType: query.MaintenanceType,
                PerformedBy: query.PerformedBy,
                SearchText: query.SearchText,
                PageSize: query.PageSize,
                Cursor: maintenanceCursor), cancellationToken);
        }

        await Task.WhenAll(masterTask, maintenanceTask).ConfigureAwait(false);
        var masterPage = await masterTask.ConfigureAwait(false);
        var maintenancePage = await maintenanceTask.ConfigureAwait(false);

        var masterRows = masterPage.Items.Select(item => new GlobalHistoryRow(
            null, item, item.OccurredAtUtc.LocalDateTime.Year, GlobalHistorySource.MasterActivity));
        var maintenanceRows = maintenancePage.Items.Select(item => new GlobalHistoryRow(
            null, ToActivityEntry(item), item.Record.ReferenceYear, GlobalHistorySource.MaintenanceActivity));

        var ordered = masterRows.Concat(maintenanceRows)
            .OrderByDescending(item => item.SortTicks)
            .ThenByDescending(item => (int)(item.ActivitySource ?? 0))
            .ThenByDescending(item => item.ReferenceYear)
            .ThenByDescending(item => item.Activity?.ActivityHistoryEntryId ?? Guid.Empty)
            .Take(query.PageSize + 1)
            .ToArray();
        var items = ordered.Take(query.PageSize).ToArray();
        var hasMore = ordered.Length > query.PageSize || masterPage.HasMore || maintenancePage.HasMore;
        var last = items.LastOrDefault();
        GlobalHistoryPageCursor? next = null;
        if (hasMore && last?.Activity is not null && last.ActivitySource.HasValue)
        {
            var source = last.ActivitySource.Value;
            var countAtGroup = items.Count(item => item.SortTicks == last.SortTicks
                && item.ActivitySource == source
                && (source != GlobalHistorySource.MaintenanceActivity || item.ReferenceYear == last.ReferenceYear));
            var inherited = cursor is not null
                && cursor.AllActivityOccurredAtUtcTicks == last.SortTicks
                && cursor.AllActivitySource == source
                && (source != GlobalHistorySource.MaintenanceActivity || cursor.AllActivityReferenceYear == last.ReferenceYear)
                    ? cursor.AllActivityOffsetAtTimestamp
                    : 0;
            next = new GlobalHistoryPageCursor(
                AllActivityOccurredAtUtcTicks: last.SortTicks,
                AllActivitySource: source,
                AllActivityReferenceYear: last.ReferenceYear,
                AllActivityOffsetAtTimestamp: inherited + countAtGroup);
        }

        return new GlobalHistoryPage(items, next, hasMore);
    }

    private static ActivityHistoryEntry ToActivityEntry(MaintenanceActivityEvent item)
    {
        var type = item.Activity.ActivityType switch
        {
            MaintenanceActivityTypes.Created => ActivityTypes.MaintenanceCreated,
            MaintenanceActivityTypes.Edited => ActivityTypes.MaintenanceEdited,
            MaintenanceActivityTypes.MarkedInvalid => ActivityTypes.MaintenanceMarkedInvalid,
            _ => item.Activity.ActivityType
        };

        return new ActivityHistoryEntry
        {
            ActivityHistoryEntryId = item.Activity.MaintenanceActivityId,
            OccurredAtUtc = item.Activity.OccurredAtUtc,
            OccurredAtUtcTicks = item.Activity.OccurredAtUtcTicks,
            ActivityType = type,
            RecordType = ActivityRecordTypes.Maintenance,
            RecordId = item.Record.MaintenanceRecordId,
            Reference = item.Record.MaintenanceNumber,
            Site = item.Record.Site,
            Location = item.Record.Location,
            Subject = item.Record.Subject,
            Description = string.IsNullOrWhiteSpace(item.Activity.Reason) ? type : item.Activity.Reason,
            ChangedBy = item.Activity.ChangedBy,
            Revision = item.Activity.Revision,
            Changes = item.Activity.Changes
        };
    }

    private static string? MapMaintenanceActivityFilter(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return string.Empty;
        if (string.Equals(normalized, ActivityTypes.MaintenanceCreated, StringComparison.Ordinal)) return MaintenanceActivityTypes.Created;
        if (string.Equals(normalized, ActivityTypes.MaintenanceEdited, StringComparison.Ordinal)) return MaintenanceActivityTypes.Edited;
        if (string.Equals(normalized, ActivityTypes.MaintenanceMarkedInvalid, StringComparison.Ordinal)) return MaintenanceActivityTypes.MarkedInvalid;
        if (string.Equals(normalized, MaintenanceActivityTypes.Created, StringComparison.Ordinal)
            || string.Equals(normalized, MaintenanceActivityTypes.Edited, StringComparison.Ordinal)
            || string.Equals(normalized, MaintenanceActivityTypes.MarkedInvalid, StringComparison.Ordinal)) return normalized;
        return null;
    }

    private static DateTimeOffset? MinDate(DateTimeOffset? current, DateTimeOffset candidate) =>
        !current.HasValue || candidate < current.Value ? candidate : current;

    private static DateTimeOffset SafePreviousTick(DateTimeOffset value) =>
        value.UtcTicks <= DateTimeOffset.MinValue.UtcTicks ? value : value.AddTicks(-1);
}
