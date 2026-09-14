using GeneralMaintenanceManager.Core.Entities;

namespace GeneralMaintenanceManager.Core.Models;

public static class GlobalHistoryModes
{
    public const string Maintenance = "Maintenance";
    public const string WorkOrders = "WorkOrders";
    public const string PreventiveMaintenance = "PreventiveMaintenance";
    public const string AllActivity = "AllActivity";
}

public enum GlobalHistorySource
{
    MasterActivity = 2,
    MaintenanceActivity = 1
}

/// <summary>
/// One bounded history/report query. All filters are applied before materialization so a
/// WPF screen never needs a complete multi-year corpus to answer an interactive request.
/// </summary>
public sealed record GlobalHistoryQuery(
    string Mode = GlobalHistoryModes.Maintenance,
    int? Year = null,
    DateTimeOffset? FromInclusive = null,
    DateTimeOffset? ToInclusive = null,
    string Site = "",
    string Location = "",
    string MaintenanceType = "",
    string ActivityType = "",
    string PerformedBy = "",
    string SearchText = "",
    bool IncludeInvalid = true,
    int PageSize = 100,
    GlobalHistoryPageCursor? Cursor = null);

/// <summary>
/// Mode-aware continuation state. Maintenance mode uses MaintenanceCursor. Work Order and
/// Preventive modes use ActivityCursor. All Activity uses the global source/timestamp cursor.
/// </summary>
public sealed record GlobalHistoryPageCursor(
    MaintenancePageCursor? MaintenanceCursor = null,
    ActivityHistoryPageCursor? ActivityCursor = null,
    long? AllActivityOccurredAtUtcTicks = null,
    GlobalHistorySource? AllActivitySource = null,
    int AllActivityReferenceYear = 0,
    int AllActivityOffsetAtTimestamp = 0);

/// <summary>Transport row used by Infrastructure without taking a dependency on WPF.</summary>
public sealed record GlobalHistoryRow(
    MaintenanceRecord? MaintenanceRecord,
    ActivityHistoryEntry? Activity,
    int ReferenceYear = 0,
    GlobalHistorySource? ActivitySource = null)
{
    public long SortTicks => MaintenanceRecord?.OccurredAtUtcTicks ?? Activity?.OccurredAtUtcTicks ?? 0L;
}

public sealed record GlobalHistoryPage(
    IReadOnlyList<GlobalHistoryRow> Items,
    GlobalHistoryPageCursor? NextCursor,
    bool HasMore);
