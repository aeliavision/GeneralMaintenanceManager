using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class ActivityHistoryService(DatabaseContextFactory contextFactory) : IActivityHistoryService
{
    private const int MaximumPageSize = 200;

    public async Task<ActivityHistoryPage> GetPageAsync(ActivityHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Year.HasValue) DataPaths.ValidateYear(query.Year.Value);
        if (query.FromInclusive.HasValue && query.ToInclusive.HasValue && query.FromInclusive > query.ToInclusive)
            throw new ArgumentException("Activity History date range is invalid.", nameof(query));

        await using var context = contextFactory.CreateMasterDbContext();
        IQueryable<ActivityHistoryEntry> rows;
        var useSearchIndex = !string.IsNullOrWhiteSpace(query.SearchText)
            && await IsSearchIndexReadyAsync(context, cancellationToken).ConfigureAwait(false);
        if (useSearchIndex)
        {
            var ftsQuery = BuildFtsQuery(query.SearchText);
            rows = context.ActivityHistory.FromSqlInterpolated($$"""
                SELECT a.*
                FROM ActivityHistory AS a
                INNER JOIN ActivityHistorySearch AS f ON f.rowid = a.rowid
                WHERE ActivityHistorySearch MATCH {{ftsQuery}}
                """).AsNoTracking();
        }
        else
        {
            rows = context.ActivityHistory.AsNoTracking();
        }

        DateTimeOffset? from = query.FromInclusive;
        DateTimeOffset? to = query.ToInclusive;
        if (query.Year.HasValue)
        {
            var start = new DateTimeOffset(new DateTime(query.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Local));
            var end = start.AddYears(1).AddTicks(-1);
            if (!from.HasValue || from.Value < start) from = start;
            if (!to.HasValue || to.Value > end) to = end;
        }

        if (from.HasValue)
        {
            var ticks = from.Value.UtcTicks;
            rows = rows.Where(item => item.OccurredAtUtcTicks >= ticks);
        }
        if (to.HasValue)
        {
            var ticks = to.Value.UtcTicks;
            rows = rows.Where(item => item.OccurredAtUtcTicks <= ticks);
        }
        if (!string.IsNullOrWhiteSpace(query.RecordType))
        {
            var value = query.RecordType.Trim();
            rows = rows.Where(item => item.RecordType == value);
        }
        if (!string.IsNullOrWhiteSpace(query.ActivityType))
        {
            var value = query.ActivityType.Trim();
            rows = rows.Where(item => item.ActivityType == value);
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
        if (!string.IsNullOrWhiteSpace(query.ChangedBy))
        {
            var value = query.ChangedBy.Trim();
            rows = rows.Where(item => item.ChangedBy == value);
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText) && !useSearchIndex)
        {
            var value = query.SearchText.Trim();
            rows = rows.Where(item => item.Reference.Contains(value)
                || item.Subject.Contains(value)
                || item.Description.Contains(value)
                || item.Site.Contains(value)
                || item.Location.Contains(value)
                || item.ChangedBy.Contains(value)
                || item.Changes.Contains(value)
                || item.ActivityType.Contains(value));
        }

        var skipAtTimestamp = 0;
        if (query.Cursor is not null)
        {
            var cursorTicks = query.Cursor.OccurredAtUtcTicks;
            rows = rows.Where(item => item.OccurredAtUtcTicks <= cursorTicks);
            skipAtTimestamp = Math.Max(query.Cursor.OffsetAtTimestamp, 0);
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var take = pageSize + skipAtTimestamp + 1;
        var materialized = await rows
            .OrderByDescending(item => item.OccurredAtUtcTicks)
            .ThenByDescending(item => item.ActivityHistoryEntryId)
            .Take(take)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<ActivityHistoryEntry> remaining = materialized;
        if (query.Cursor is not null && skipAtTimestamp > 0)
        {
            remaining = remaining.Skip(skipAtTimestamp);
        }

        var window = remaining.Take(pageSize + 1).ToArray();
        var hasMore = window.Length > pageSize;
        var items = window.Take(pageSize).ToArray();
        var last = items.LastOrDefault();
        ActivityHistoryPageCursor? next = null;
        if (hasMore && last is not null)
        {
            var pageCountAtLastTimestamp = items.Count(item => item.OccurredAtUtcTicks == last.OccurredAtUtcTicks);
            var inheritedOffset = query.Cursor is not null && query.Cursor.OccurredAtUtcTicks == last.OccurredAtUtcTicks
                ? query.Cursor.OffsetAtTimestamp
                : 0;
            next = new ActivityHistoryPageCursor(last.OccurredAtUtcTicks, inheritedOffset + pageCountAtLastTimestamp);
        }

        return new ActivityHistoryPage(items, next, hasMore);
    }

    public async Task<IReadOnlyList<ActivityHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ActivityHistoryEntry>();
        ActivityHistoryPageCursor? cursor = null;
        while (true)
        {
            var page = await GetPageAsync(new ActivityHistoryQuery(PageSize: MaximumPageSize, Cursor: cursor), cancellationToken).ConfigureAwait(false);
            results.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null) break;
            if (page.NextCursor == cursor) throw new InvalidOperationException("Activity History paging did not advance.");
            cursor = page.NextCursor;
        }
        return results;
    }

    public async Task<IReadOnlyList<ActivityHistoryEntry>> GetForYearAsync(int year, CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        var results = new List<ActivityHistoryEntry>();
        ActivityHistoryPageCursor? cursor = null;
        while (true)
        {
            var page = await GetPageAsync(new ActivityHistoryQuery(Year: year, PageSize: MaximumPageSize, Cursor: cursor), cancellationToken).ConfigureAwait(false);
            results.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null) break;
            if (page.NextCursor == cursor) throw new InvalidOperationException("Activity History paging did not advance.");
            cursor = page.NextCursor;
        }
        return results;
    }

    public async Task<IReadOnlyList<ActivityHistoryEntry>> GetForRecordAsync(
        string recordType,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = Clean(recordType);
        if (normalizedType.Length == 0 || recordId == Guid.Empty)
        {
            return Array.Empty<ActivityHistoryEntry>();
        }

        await using var context = contextFactory.CreateMasterDbContext();
        var newest = await context.ActivityHistory.AsNoTracking()
            .Where(item => item.RecordType == normalizedType && item.RecordId == recordId)
            .OrderByDescending(item => item.OccurredAtUtcTicks)
            .ThenByDescending(item => item.ActivityHistoryEntryId)
            .Take(500)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return newest
            .OrderBy(item => item.OccurredAtUtcTicks)
            .ThenBy(item => item.ActivityHistoryEntryId)
            .ToArray();
    }

    public async Task<ActivityHistoryEntry> AppendAsync(ActivityHistoryDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);

        await using var context = contextFactory.CreateMasterDbContext();
        var entry = CreateEntry(draft);
        context.ActivityHistory.Add(entry);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    internal static ActivityHistoryEntry CreateEntry(ActivityHistoryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        var occurredAtUtc = draft.OccurredAtUtc ?? DateTimeOffset.UtcNow;
        return new ActivityHistoryEntry
        {
            ActivityHistoryEntryId = Guid.NewGuid(),
            OccurredAtUtc = occurredAtUtc,
            OccurredAtUtcTicks = occurredAtUtc.UtcTicks,
            ActivityType = Clean(draft.ActivityType),
            RecordType = Clean(draft.RecordType),
            RecordId = draft.RecordId,
            Reference = Clean(draft.Reference),
            Site = Clean(draft.Site),
            Location = Clean(draft.Location),
            Subject = Clean(draft.Subject),
            Description = Clean(draft.Description),
            ChangedBy = Clean(draft.ChangedBy ?? Environment.UserName),
            Revision = Math.Max(draft.Revision, 0),
            Changes = Clean(draft.Changes)
        };
    }

    private static void Validate(ActivityHistoryDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ActivityType)) throw new ArgumentException("Activity type is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.RecordType)) throw new ArgumentException("Record type is required.", nameof(draft));
        if (draft.RecordId == Guid.Empty) throw new ArgumentException("Record ID is required.", nameof(draft));
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static async Task<bool> IsSearchIndexReadyAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT IsReady FROM SearchIndexState WHERE SearchName='ActivityHistorySearch' LIMIT 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(value ?? 0, System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            if (openedHere) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string BuildFtsQuery(string value)
    {
        var tokens = Regex.Matches(value ?? string.Empty, @"[\p{L}\p{N}_]+")
            .Select(match => match.Value)
            .Where(token => token.Length > 0)
            .Take(12)
            .Select(token => $"\"{token.Replace("\"", "\"\"")}\"*")
            .ToArray();
        return tokens.Length == 0 ? "\"__no_match__\"" : string.Join(" AND ", tokens);
    }
}
