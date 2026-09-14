using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.Core.Models;

/// <summary>Bounded keyset-like query for append-only master activity history.</summary>
public sealed record ActivityHistoryQuery(
    int? Year = null,
    DateTimeOffset? FromInclusive = null,
    DateTimeOffset? ToInclusive = null,
    string RecordType = "",
    string ActivityType = "",
    string Site = "",
    string Location = "",
    string ChangedBy = "",
    string SearchText = "",
    int PageSize = 100,
    ActivityHistoryPageCursor? Cursor = null);

/// <summary>
/// Continuation cursor for append-only activity history. OffsetAtTimestamp is used as the
/// deterministic tie-break continuation for entries sharing the exact same UTC tick.
/// </summary>
public sealed record ActivityHistoryPageCursor(long OccurredAtUtcTicks, int OffsetAtTimestamp);

public sealed record ActivityHistoryPage(
    IReadOnlyList<ActivityHistoryEntry> Items,
    ActivityHistoryPageCursor? NextCursor,
    bool HasMore);
