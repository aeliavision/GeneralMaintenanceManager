namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Forward-only GMM production migration progress. This is operational metadata,
/// not canonical Maintenance history. Rows are retained permanently for auditability.
/// </summary>
internal sealed class SchemaMigrationJournalRow
{
    public string MigrationId { get; set; } = string.Empty;
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
    public string State { get; set; } = string.Empty;
    public string ProgressCursor { get; set; } = string.Empty;
    public string PreUpgradeBackupPath { get; set; } = string.Empty;
    public string PreUpgradeBackupSha256 { get; set; } = string.Empty;
    public long StartedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public long? CompletedAtUtcTicks { get; set; }
    public string LastError { get; set; } = string.Empty;
}
