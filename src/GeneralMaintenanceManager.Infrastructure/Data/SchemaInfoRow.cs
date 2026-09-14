namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Production schema identity. DB-9 freezes the first customer-preserving
/// General Maintenance Manager database contract at production schema v1.
/// Migration progress lives separately in SchemaMigrationJournal.
/// </summary>
internal sealed class SchemaInfoRow
{
    public const int SingletonId = 1;
    public const int CurrentSchemaVersion = ProductionSchemaPolicy.CurrentVersion;
    public const string MasterFormatId = "GeneralMaintenanceManager.Master";
    public const string AnnualMaintenanceFormatId = "GeneralMaintenanceManager.AnnualMaintenance";

    public int Id { get; set; } = SingletonId;
    public string FormatId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Physical annual partition identity. Null for the master database.</summary>
    public int? PartitionYear { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
