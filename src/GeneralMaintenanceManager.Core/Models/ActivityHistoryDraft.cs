namespace GeneralMaintenanceManager.Core.Models;

public sealed record ActivityHistoryDraft(
    string ActivityType,
    string RecordType,
    Guid RecordId,
    string Reference,
    string Site,
    string Location,
    string Subject,
    string Description,
    int Revision,
    string Changes = "",
    DateTimeOffset? OccurredAtUtc = null,
    string? ChangedBy = null);
