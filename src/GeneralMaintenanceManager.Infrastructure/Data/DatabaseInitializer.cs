using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Creates and validates the production master + annual database layout directly.
/// It never adopts unknown files. Recognized GMM production databases may run only registered
/// forward migrations through ProductionMigrationCoordinator before strict schema validation.
/// </summary>
public sealed class DatabaseInitializer(
    DatabaseContextFactory contextFactory,
    ProductionMigrationCoordinator? migrationCoordinator = null) : IDisposable
{
    private readonly ProductionMigrationCoordinator _migrationCoordinator = migrationCoordinator ?? new ProductionMigrationCoordinator();
    public const int MasterSchemaVersion = SchemaInfoRow.CurrentSchemaVersion;
    public const int AnnualSchemaVersion = SchemaInfoRow.CurrentSchemaVersion;
    public const string MasterFormatId = SchemaInfoRow.MasterFormatId;
    public const string AnnualFormatId = SchemaInfoRow.AnnualMaintenanceFormatId;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<int> _verifiedYears = [];
    private bool _masterVerified;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _verifiedYears.Clear();
            _masterVerified = false;
            await EnsureMasterCoreAsync(cancellationToken).ConfigureAwait(false);
            await EnsureYearCoreAsync(DateTime.Today.Year, cancellationToken).ConfigureAwait(false);
            _verifiedYears.Add(DateTime.Today.Year);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureMasterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_masterVerified) return;
            await EnsureMasterCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureYearAsync(int year, CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verifiedYears.Contains(year)) return;
            await EnsureYearCoreAsync(year, cancellationToken).ConfigureAwait(false);
            _verifiedYears.Add(year);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureMasterCoreAsync(CancellationToken cancellationToken)
    {
        var path = contextFactory.MasterDatabasePath;
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        if (existed)
        {
            await MigrateRecognizedProductionDatabaseIfRequiredAsync(
                path, MasterFormatId, null, MasterSchemaVersion, ProductionMigrationCatalog.MasterSteps, cancellationToken).ConfigureAwait(false);
        }
        await using var context = contextFactory.CreateMasterDbContext();
        await ConfigureCommonAsync(context, cancellationToken).ConfigureAwait(false);

        if (!existed)
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await InsertSchemaIdentityAsync(context, MasterFormatId, MasterSchemaVersion, null, cancellationToken).ConfigureAwait(false);
            await EnsureMasterIntegrityTriggersAsync(context, cancellationToken).ConfigureAwait(false);
        }

        await ValidateSchemaIdentityAsync(context, MasterFormatId, MasterSchemaVersion, null, path, cancellationToken).ConfigureAwait(false);
        await EnsureKnownMasterAdditiveInfrastructureAsync(context, cancellationToken).ConfigureAwait(false);
        await ValidateMasterSchemaAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureMasterIntegrityTriggersAsync(context, cancellationToken).ConfigureAwait(false);
        _masterVerified = true;
    }

    private async Task EnsureYearCoreAsync(int year, CancellationToken cancellationToken)
    {
        var path = contextFactory.GetYearDatabasePath(year);
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        if (existed)
        {
            await MigrateRecognizedProductionDatabaseIfRequiredAsync(
                path, AnnualFormatId, year, AnnualSchemaVersion, ProductionMigrationCatalog.AnnualSteps, cancellationToken).ConfigureAwait(false);
        }
        await using var context = contextFactory.CreateAnnualDbContext(year);
        await ConfigureCommonAsync(context, cancellationToken).ConfigureAwait(false);

        if (!existed)
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await InsertSchemaIdentityAsync(context, AnnualFormatId, AnnualSchemaVersion, year, cancellationToken).ConfigureAwait(false);
            await EnsureAnnualIntegrityTriggersAsync(context, year, cancellationToken).ConfigureAwait(false);
            await EnsureAnnualSearchInfrastructureAsync(context, cancellationToken).ConfigureAwait(false);
            await EnsureAnnualDashboardSummaryInfrastructureAsync(context, cancellationToken).ConfigureAwait(false);
        }

        await ValidateSchemaIdentityAsync(context, AnnualFormatId, AnnualSchemaVersion, year, path, cancellationToken).ConfigureAwait(false);
        // Production baseline: never repair/build large annual indexes on startup.
        // A missing required index is a schema failure; pre-release development Data may be reset.
        await ValidateAnnualSchemaAsync(context, year, cancellationToken).ConfigureAwait(false);
        await EnsureAnnualIntegrityTriggersAsync(context, year, cancellationToken).ConfigureAwait(false);
        await EnsureAnnualSearchInfrastructureAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateRecognizedProductionDatabaseIfRequiredAsync(
        string path,
        string expectedFormatId,
        int? expectedPartitionYear,
        int targetVersion,
        IReadOnlyCollection<ProductionMigrationStep> steps,
        CancellationToken cancellationToken)
    {
        var identity = await TryReadProductionIdentityAsync(path, cancellationToken).ConfigureAwait(false);
        if (identity is null
            || !string.Equals(identity.Value.FormatId, expectedFormatId, StringComparison.Ordinal)
            || identity.Value.PartitionYear != expectedPartitionYear
            || identity.Value.SchemaVersion >= targetVersion
            || identity.Value.SchemaVersion < ProductionSchemaPolicy.BaselineVersion)
        {
            return;
        }

        var candidateSteps = steps.Where(step => step.FromVersion == identity.Value.SchemaVersion).ToArray();
        string? resumeBackupPath = null;
        if (candidateSteps.Length == 1)
        {
            resumeBackupPath = await TryReadPendingMigrationBackupPathAsync(
                path, candidateSteps[0].MigrationId, cancellationToken).ConfigureAwait(false);
        }

        var backupPath = resumeBackupPath
            ?? await CreatePreUpgradeBackupAsync(path, identity.Value.SchemaVersion, cancellationToken).ConfigureAwait(false);
        await _migrationCoordinator.MigrateAsync(
            new ProductionMigrationRequest(path, expectedFormatId, expectedPartitionYear, targetVersion, backupPath),
            steps,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> TryReadPendingMigrationBackupPathAsync(
        string databasePath,
        string migrationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT PreUpgradeBackupPath FROM SchemaMigrationJournal " +
                "WHERE MigrationId=$id AND State <> 'Completed' LIMIT 1;";
            command.Parameters.AddWithValue("$id", migrationId);
            var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SqliteException)
        {
            // Strict validation/coordinator diagnostics remain authoritative for malformed or
            // unsupported databases. Do not adopt an unknown database just to recover a path.
            return null;
        }
    }

    private async Task<string> CreatePreUpgradeBackupAsync(
        string databasePath,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(contextFactory.BackupsDirectory);
        var fileName = $"{Path.GetFileNameWithoutExtension(databasePath)}-preupgrade-v{schemaVersion}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
        var backupPath = Path.Combine(contextFactory.BackupsDirectory, fileName);

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await using var destination = new SqliteConnection(destinationBuilder.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task<(string FormatId, int SchemaVersion, int? PartitionYear)?> TryReadProductionIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT FormatId, SchemaVersion, PartitionYear FROM SchemaInfo WHERE Id=1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static async Task ConfigureCommonAsync(DbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSchemaIdentityAsync(
        DbContext context,
        string formatId,
        int schemaVersion,
        int? partitionYear,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "SchemaInfo", cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The clean database schema was not created correctly. Delete the development database and start again.");
        }

        var rows = await context.Set<SchemaInfoRow>().CountAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 0) return;

        context.Set<SchemaInfoRow>().Add(new SchemaInfoRow
        {
            Id = SchemaInfoRow.SingletonId,
            FormatId = formatId,
            SchemaVersion = schemaVersion,
            PartitionYear = partitionYear,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSchemaIdentityAsync(
        DbContext context,
        string expectedFormat,
        int expectedVersion,
        int? expectedPartitionYear,
        string path,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "SchemaInfo", cancellationToken).ConfigureAwait(false))
        {
            throw Incompatible(path, "SchemaInfo is missing");
        }

        var identities = await context.Set<SchemaInfoRow>().AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (identities.Length != 1
            || identities[0].Id != SchemaInfoRow.SingletonId
            || !string.Equals(identities[0].FormatId, expectedFormat, StringComparison.Ordinal)
            || identities[0].SchemaVersion != expectedVersion
            || identities[0].PartitionYear != expectedPartitionYear)
        {
            throw Incompatible(path, $"expected {expectedFormat} schema version {expectedVersion} partition {(expectedPartitionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "master")}");
        }
    }

    private static async Task ValidateMasterSchemaAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        string[] requiredTables =
        [
            "SchemaInfo", "SchemaMigrationJournal", "Asset", "ReviewItems", "AssetAudit", "WorkOrders", "WorkOrderAudit",
            "MaintenancePlans", "MaintenancePlanAudit", "WorkOrderCompletionIntents", "ActivityHistory", "MaintenanceNumberSequence", "WorkOrderNumberSequence",
            "MaintenancePlanNumberSequence"
        ];
        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(context, table, cancellationToken).ConfigureAwait(false))
                throw Incompatible(context.Database.GetDbConnection().DataSource, $"missing required table '{table}'");
        }

        if (await TableExistsAsync(context, "MaintenanceRecords", cancellationToken).ConfigureAwait(false)
            || await TableExistsAsync(context, "MaintenanceEvents", cancellationToken).ConfigureAwait(false)
            || await TableExistsAsync(context, "MachineEvents", cancellationToken).ConfigureAwait(false))
        {
            throw Incompatible(context.Database.GetDbConnection().DataSource, "legacy/canonical Maintenance tables are not allowed in the master database");
        }

        string[] requiredIndexes =
        [
            "IX_SchemaMigrationJournal_FromVersion_ToVersion", "IX_SchemaMigrationJournal_State",
            "IX_WorkOrders_WorkOrderNumber", "IX_WorkOrders_ReferenceYear_ReferenceSequence", "IX_WorkOrders_MaintenancePlanId",
            "IX_WorkOrders_GeneratedMaintenanceRecordId", "IX_WorkOrders_GeneratedMaintenanceNumber",
            "IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence",
            "IX_WorkOrders_ReferenceYear_ActivityAtUtcTicks_ReferenceSequence",
            "IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_RecordType_RecordId_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_ActivityType_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "IX_MaintenancePlans_MaintenancePlanNumber", "IX_MaintenancePlans_IsActive_NextDueDate", "IX_WorkOrderCompletionIntents_WorkOrderId",
            "IX_WorkOrderCompletionIntents_MaintenanceRecordId", "IX_WorkOrderCompletionIntents_MaintenanceNumber"
        ];
        await RequireObjectsAsync(context, "index", requiredIndexes, cancellationToken).ConfigureAwait(false);
        await RequireObjectsAsync(context, "table", ["PreventiveDueOccurrence", "SearchIndexState", "WorkOrderSearch", "ActivityHistorySearch"], cancellationToken).ConfigureAwait(false);
        await RequireObjectsAsync(context, "trigger",
            [
                "TR_WorkOrderSearch_Insert", "TR_WorkOrderSearch_Update", "TR_WorkOrderSearch_Delete",
                "TR_ActivityHistorySearch_Insert", "TR_ActivityHistorySearch_Update", "TR_ActivityHistorySearch_Delete",
                "TR_WorkOrders_ImmutableReference", "TR_WorkOrders_SyncSequence_AfterInsert",
                "TR_WorkOrderNumberSequence_BlockDecrease", "TR_WorkOrderNumberSequence_BlockDelete",
                "TR_MaintenanceNumberSequence_BlockDecrease", "TR_MaintenanceNumberSequence_BlockDelete",
                "TR_MaintenancePlanNumberSequence_BlockDecrease", "TR_MaintenancePlanNumberSequence_BlockDelete"
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateWorkOrderSearchContractAsync(context, cancellationToken).ConfigureAwait(false);
        await ValidateActivityHistorySearchContractAsync(context, cancellationToken).ConfigureAwait(false);
    }



    private static async Task ValidateAnnualSchemaAsync(AnnualDbContext context, int year, CancellationToken cancellationToken)
    {
        string[] requiredTables = ["SchemaInfo", "SchemaMigrationJournal", "MaintenanceRecords", "MaintenanceActivity", "MaintenanceLocationSummary", "MaintenanceTypeSummary"];
        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(context, table, cancellationToken).ConfigureAwait(false))
                throw Incompatible(context.Database.GetDbConnection().DataSource, $"missing required table '{table}'");
        }

        if (await TableExistsAsync(context, "MaintenanceEvents", cancellationToken).ConfigureAwait(false)
            || await TableExistsAsync(context, "MachineEvents", cancellationToken).ConfigureAwait(false)
            || await TableExistsAsync(context, "MaintenanceEventAudit", cancellationToken).ConfigureAwait(false))
        {
            throw Incompatible(context.Database.GetDbConnection().DataSource, "unsupported legacy annual tables were detected");
        }

        string[] requiredIndexes =
        [
            "IX_SchemaMigrationJournal_FromVersion_ToVersion", "IX_SchemaMigrationJournal_State",
            "IX_MaintenanceRecords_MaintenanceNumber",
            "IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_Location_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_Site_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_MaintenanceType_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_AssetId_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_PerformedBy_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
            "IX_MaintenanceRecords_GeneratedByWorkOrderId",
            "IX_MaintenanceActivity_MaintenanceRecordId_OccurredAtUtcTicks",
            "IX_MaintenanceActivity_OccurredAtUtcTicks_MaintenanceActivityId",
            "IX_MaintenanceActivity_ActivityType_OccurredAtUtcTicks_MaintenanceActivityId",
            "IX_MaintenanceLocationSummary_RecordCount_Label",
            "IX_MaintenanceTypeSummary_RecordCount_Label"
        ];
        await RequireObjectsAsync(context, "index", requiredIndexes, cancellationToken).ConfigureAwait(false);
        await RequireObjectsAsync(context, "table", ["MaintenanceSearch"], cancellationToken).ConfigureAwait(false);
        await RequireObjectsAsync(context, "trigger",
            ["TR_MaintenanceSearch_Insert", "TR_MaintenanceSearch_Update", "TR_MaintenanceDashboardSummary_Insert", "TR_MaintenanceDashboardSummary_Update", "TR_SchemaMigrationJournal_BlockDelete"],
            cancellationToken).ConfigureAwait(false);
    }



    private static async Task EnsureMasterIntegrityTriggersAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS TR_SchemaInfo_BlockUpdate
            BEFORE UPDATE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_SchemaInfo_BlockDelete
            BEFORE DELETE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_SchemaMigrationJournal_BlockDelete
            BEFORE DELETE ON SchemaMigrationJournal BEGIN SELECT RAISE(ABORT, 'Production migration history is permanent.'); END;

            CREATE TRIGGER IF NOT EXISTS TR_ActivityHistory_BlockUpdate
            BEFORE UPDATE ON ActivityHistory BEGIN SELECT RAISE(ABORT, 'Activity History is append-only.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_ActivityHistory_BlockDelete
            BEFORE DELETE ON ActivityHistory BEGIN SELECT RAISE(ABORT, 'Activity History is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_AssetAudit_BlockUpdate
            BEFORE UPDATE ON AssetAudit BEGIN SELECT RAISE(ABORT, 'Asset audit history is append-only.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_AssetAudit_BlockDelete
            BEFORE DELETE ON AssetAudit BEGIN SELECT RAISE(ABORT, 'Asset audit history is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderAudit_BlockUpdate
            BEFORE UPDATE ON WorkOrderAudit BEGIN SELECT RAISE(ABORT, 'Work Order audit history is append-only.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderAudit_BlockDelete
            BEFORE DELETE ON WorkOrderAudit BEGIN SELECT RAISE(ABORT, 'Work Order audit history is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlanAudit_BlockUpdate
            BEFORE UPDATE ON MaintenancePlanAudit BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance audit history is append-only.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlanAudit_BlockDelete
            BEFORE DELETE ON MaintenancePlanAudit BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance audit history is permanent.'); END;

            CREATE TRIGGER IF NOT EXISTS TR_WorkOrders_BlockDelete
            BEFORE DELETE ON WorkOrders BEGIN SELECT RAISE(ABORT, 'Work Orders must be cancelled, not deleted.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_WorkOrders_ImmutableReference
            BEFORE UPDATE ON WorkOrders
            WHEN hex(NEW.WorkOrderNumber) <> hex(OLD.WorkOrderNumber)
                 OR NEW.ReferenceYear <> OLD.ReferenceYear
                 OR NEW.ReferenceSequence <> OLD.ReferenceSequence
                 OR NEW.ReportedAtUtcTicks <> OLD.ReportedAtUtcTicks
            BEGIN SELECT RAISE(ABORT, 'Work Order reference identity is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_WorkOrders_SyncSequence_AfterInsert
            AFTER INSERT ON WorkOrders
            BEGIN
                INSERT INTO WorkOrderNumberSequence(Year, LastValue)
                VALUES(NEW.ReferenceYear, NEW.ReferenceSequence)
                ON CONFLICT(Year) DO UPDATE
                SET LastValue = max(WorkOrderNumberSequence.LastValue, excluded.LastValue);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderNumberSequence_BlockDecrease
            BEFORE UPDATE OF LastValue ON WorkOrderNumberSequence
            WHEN NEW.LastValue < OLD.LastValue
            BEGIN SELECT RAISE(ABORT, 'Work Order sequence cannot move backwards.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderNumberSequence_BlockDelete
            BEFORE DELETE ON WorkOrderNumberSequence
            BEGIN SELECT RAISE(ABORT, 'Work Order sequence state is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceNumberSequence_BlockDecrease
            BEFORE UPDATE OF LastValue ON MaintenanceNumberSequence
            WHEN NEW.LastValue < OLD.LastValue
            BEGIN SELECT RAISE(ABORT, 'Maintenance sequence cannot move backwards.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceNumberSequence_BlockDelete
            BEFORE DELETE ON MaintenanceNumberSequence
            BEGIN SELECT RAISE(ABORT, 'Maintenance sequence state is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlanNumberSequence_BlockDecrease
            BEFORE UPDATE OF LastValue ON MaintenancePlanNumberSequence
            WHEN NEW.LastValue < OLD.LastValue
            BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance sequence cannot move backwards.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlanNumberSequence_BlockDelete
            BEFORE DELETE ON MaintenancePlanNumberSequence
            BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance sequence state is permanent.'); END;

            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlans_BlockDelete
            BEFORE DELETE ON MaintenancePlans BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance plans must be paused, not deleted.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenancePlans_ImmutableReference
            BEFORE UPDATE ON MaintenancePlans WHEN NEW.MaintenancePlanNumber <> OLD.MaintenancePlanNumber
            BEGIN SELECT RAISE(ABORT, 'Preventive Maintenance references are permanent.'); END;
            """,
            cancellationToken).ConfigureAwait(false);
    }



    private static async Task EnsureAnnualIntegrityTriggersAsync(AnnualDbContext context, int year, CancellationToken cancellationToken)
    {
        var sql = $$"""
            CREATE TRIGGER IF NOT EXISTS TR_SchemaInfo_BlockUpdate
            BEFORE UPDATE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_SchemaInfo_BlockDelete
            BEFORE DELETE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_SchemaMigrationJournal_BlockDelete
            BEFORE DELETE ON SchemaMigrationJournal BEGIN SELECT RAISE(ABORT, 'Production migration history is permanent.'); END;

            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceActivity_BlockUpdate
            BEFORE UPDATE ON MaintenanceActivity BEGIN SELECT RAISE(ABORT, 'Maintenance activity is append-only.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceActivity_BlockDelete
            BEFORE DELETE ON MaintenanceActivity BEGIN SELECT RAISE(ABORT, 'Maintenance activity is permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_BlockDelete
            BEFORE DELETE ON MaintenanceRecords BEGIN SELECT RAISE(ABORT, 'Maintenance Records must be marked invalid, not deleted.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_ImmutableIdentity
            BEFORE UPDATE ON MaintenanceRecords
            WHEN NEW.MaintenanceRecordId <> OLD.MaintenanceRecordId
                 OR NEW.MaintenanceNumber <> OLD.MaintenanceNumber
                 OR NEW.ReferenceYear <> OLD.ReferenceYear
                 OR NEW.ReferenceSequence <> OLD.ReferenceSequence
                 OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc
                 OR NEW.CreatedAtUtcTicks <> OLD.CreatedAtUtcTicks
                 OR NEW.CreatedBy <> OLD.CreatedBy
                 OR COALESCE(NEW.GeneratedByWorkOrderId, '') <> COALESCE(OLD.GeneratedByWorkOrderId, '')
                 OR COALESCE(NEW.WorkOrderNumberSnapshot, '') <> COALESCE(OLD.WorkOrderNumberSnapshot, '')
                 OR COALESCE(NEW.AssetId, '') <> COALESCE(OLD.AssetId, '')
                 OR COALESCE(NEW.AssetNumberSnapshot, '') <> COALESCE(OLD.AssetNumberSnapshot, '')
                 OR COALESCE(NEW.AssetNameSnapshot, '') <> COALESCE(OLD.AssetNameSnapshot, '')
            BEGIN SELECT RAISE(ABORT, 'Maintenance identity, partition and source snapshots are permanent.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_ReferenceYear_Insert
            BEFORE INSERT ON MaintenanceRecords WHEN NEW.ReferenceYear <> {{year}}
            BEGIN SELECT RAISE(ABORT, 'Maintenance reference year does not match this annual database.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_ReferenceYear_Update
            BEFORE UPDATE ON MaintenanceRecords WHEN NEW.ReferenceYear <> {{year}}
            BEGIN SELECT RAISE(ABORT, 'Maintenance reference year cannot move between annual databases.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_ReferenceFormat_Insert
            BEFORE INSERT ON MaintenanceRecords
            WHEN NEW.MaintenanceNumber <> printf('MNT-%04d-%07d', NEW.ReferenceYear, NEW.ReferenceSequence)
            BEGIN SELECT RAISE(ABORT, 'Maintenance reference does not match its permanent year/sequence.'); END;
            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceRecords_ReferenceFormat_Update
            BEFORE UPDATE ON MaintenanceRecords
            WHEN NEW.MaintenanceNumber <> printf('MNT-%04d-%07d', NEW.ReferenceYear, NEW.ReferenceSequence)
            BEGIN SELECT RAISE(ABORT, 'Maintenance reference does not match its permanent year/sequence.'); END;
            """;
        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateWorkOrderSearchContractAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='WorkOrderSearch';";
            var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!sql.Contains("content='WorkOrders'", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("content_rowid='rowid'", StringComparison.OrdinalIgnoreCase))
            {
                throw Incompatible(
                    context.Database.GetDbConnection().DataSource,
                    "obsolete pre-release WorkOrder FTS layout detected; delete the development Data folder and restart to create the production baseline");
            }
        }
        finally
        {
            if (openedHere) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task ValidateActivityHistorySearchContractAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ActivityHistorySearch';";
            var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!sql.Contains("content='ActivityHistory'", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("content_rowid='rowid'", StringComparison.OrdinalIgnoreCase))
            {
                throw Incompatible(
                    context.Database.GetDbConnection().DataSource,
                    "obsolete pre-release Activity History FTS layout detected; delete the development Data folder and restart to create the production baseline");
            }
        }
        finally
        {
            if (openedHere) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task EnsureKnownMasterAdditiveInfrastructureAsync(MasterDbContext context, CancellationToken cancellationToken)
    {
        // O(1) startup contract: create only small additive structures and triggers here.
        // Existing Work Orders are never scanned/backfilled on startup. A full FTS rebuild is
        // an explicit Database & Maintenance action. GMM is pre-release, so legacy development
        // Data folders should be reset when adopting this production baseline.
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS PreventiveDueOccurrence(
                MaintenancePlanId TEXT NOT NULL,
                OccurrenceKey TEXT NOT NULL,
                RecordedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (MaintenancePlanId, OccurrenceKey)
            );

            CREATE TABLE IF NOT EXISTS SearchIndexState(
                SearchName TEXT NOT NULL PRIMARY KEY,
                IsReady INTEGER NOT NULL CHECK(IsReady IN (0, 1))
            );
            INSERT OR IGNORE INTO SearchIndexState(SearchName, IsReady) VALUES('ActivityHistorySearch', 0);
            UPDATE SearchIndexState
            SET IsReady = 1
            WHERE SearchName = 'ActivityHistorySearch'
              AND NOT EXISTS(SELECT 1 FROM ActivityHistory LIMIT 1);

            CREATE VIRTUAL TABLE IF NOT EXISTS WorkOrderSearch USING fts5(
                WorkOrderNumber,
                Title,
                ProblemDescription,
                Site,
                Location,
                AssignedTo,
                PerformedBy,
                AssetNumber,
                GeneratedMaintenanceNumber,
                WorkPerformed,
                Notes,
                content='WorkOrders',
                content_rowid='rowid',
                tokenize='unicode61'
            );

            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderSearch_Insert
            AFTER INSERT ON WorkOrders BEGIN
                INSERT INTO WorkOrderSearch(
                    rowid, WorkOrderNumber, Title, ProblemDescription, Site, Location,
                    AssignedTo, PerformedBy, AssetNumber, GeneratedMaintenanceNumber, WorkPerformed, Notes)
                VALUES(
                    NEW.rowid, NEW.WorkOrderNumber, NEW.Title, NEW.ProblemDescription, NEW.Site, NEW.Location,
                    NEW.AssignedTo, NEW.PerformedBy, NEW.AssetNumber, NEW.GeneratedMaintenanceNumber, NEW.WorkPerformed, NEW.Notes);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderSearch_Update
            AFTER UPDATE ON WorkOrders BEGIN
                INSERT INTO WorkOrderSearch(
                    WorkOrderSearch, rowid, WorkOrderNumber, Title, ProblemDescription, Site, Location,
                    AssignedTo, PerformedBy, AssetNumber, GeneratedMaintenanceNumber, WorkPerformed, Notes)
                VALUES(
                    'delete', OLD.rowid, OLD.WorkOrderNumber, OLD.Title, OLD.ProblemDescription, OLD.Site, OLD.Location,
                    OLD.AssignedTo, OLD.PerformedBy, OLD.AssetNumber, OLD.GeneratedMaintenanceNumber, OLD.WorkPerformed, OLD.Notes);
                INSERT INTO WorkOrderSearch(
                    rowid, WorkOrderNumber, Title, ProblemDescription, Site, Location,
                    AssignedTo, PerformedBy, AssetNumber, GeneratedMaintenanceNumber, WorkPerformed, Notes)
                VALUES(
                    NEW.rowid, NEW.WorkOrderNumber, NEW.Title, NEW.ProblemDescription, NEW.Site, NEW.Location,
                    NEW.AssignedTo, NEW.PerformedBy, NEW.AssetNumber, NEW.GeneratedMaintenanceNumber, NEW.WorkPerformed, NEW.Notes);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_WorkOrderSearch_Delete
            AFTER DELETE ON WorkOrders BEGIN
                INSERT INTO WorkOrderSearch(
                    WorkOrderSearch, rowid, WorkOrderNumber, Title, ProblemDescription, Site, Location,
                    AssignedTo, PerformedBy, AssetNumber, GeneratedMaintenanceNumber, WorkPerformed, Notes)
                VALUES(
                    'delete', OLD.rowid, OLD.WorkOrderNumber, OLD.Title, OLD.ProblemDescription, OLD.Site, OLD.Location,
                    OLD.AssignedTo, OLD.PerformedBy, OLD.AssetNumber, OLD.GeneratedMaintenanceNumber, OLD.WorkPerformed, OLD.Notes);
            END;

            CREATE VIRTUAL TABLE IF NOT EXISTS ActivityHistorySearch USING fts5(
                ActivityType,
                RecordType,
                Reference,
                Site,
                Location,
                Subject,
                Description,
                ChangedBy,
                Changes,
                content='ActivityHistory',
                content_rowid='rowid',
                tokenize='unicode61'
            );

            CREATE TRIGGER IF NOT EXISTS TR_ActivityHistorySearch_Insert
            AFTER INSERT ON ActivityHistory BEGIN
                INSERT INTO ActivityHistorySearch(
                    rowid, ActivityType, RecordType, Reference, Site, Location, Subject, Description, ChangedBy, Changes)
                VALUES(
                    NEW.rowid, NEW.ActivityType, NEW.RecordType, NEW.Reference, NEW.Site, NEW.Location,
                    NEW.Subject, NEW.Description, NEW.ChangedBy, NEW.Changes);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_ActivityHistorySearch_Update
            AFTER UPDATE ON ActivityHistory BEGIN
                INSERT INTO ActivityHistorySearch(
                    ActivityHistorySearch, rowid, ActivityType, RecordType, Reference, Site, Location, Subject, Description, ChangedBy, Changes)
                VALUES(
                    'delete', OLD.rowid, OLD.ActivityType, OLD.RecordType, OLD.Reference, OLD.Site, OLD.Location,
                    OLD.Subject, OLD.Description, OLD.ChangedBy, OLD.Changes);
                INSERT INTO ActivityHistorySearch(
                    rowid, ActivityType, RecordType, Reference, Site, Location, Subject, Description, ChangedBy, Changes)
                VALUES(
                    NEW.rowid, NEW.ActivityType, NEW.RecordType, NEW.Reference, NEW.Site, NEW.Location,
                    NEW.Subject, NEW.Description, NEW.ChangedBy, NEW.Changes);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_ActivityHistorySearch_Delete
            AFTER DELETE ON ActivityHistory BEGIN
                INSERT INTO ActivityHistorySearch(
                    ActivityHistorySearch, rowid, ActivityType, RecordType, Reference, Site, Location, Subject, Description, ChangedBy, Changes)
                VALUES(
                    'delete', OLD.rowid, OLD.ActivityType, OLD.RecordType, OLD.Reference, OLD.Site, OLD.Location,
                    OLD.Subject, OLD.Description, OLD.ChangedBy, OLD.Changes);
            END;
            """,
            cancellationToken).ConfigureAwait(false);
    }


    internal static async Task EnsureAnnualSearchInfrastructureAsync(AnnualDbContext context, CancellationToken cancellationToken)
    {
        // FTS5 is part of the bundled Microsoft.Data.Sqlite runtime. Failure here is intentional:
        // a clean annual schema without its scalable search index is not accepted.
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS MaintenanceSearch USING fts5(
                MaintenanceRecordId UNINDEXED,
                MaintenanceNumber,
                Site,
                Location,
                Subject,
                MaintenanceType,
                WorkPerformed,
                PerformedBy,
                Notes,
                AssetNumberSnapshot,
                AssetNameSnapshot,
                WorkOrderNumberSnapshot,
                tokenize='unicode61'
            );

            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceSearch_Insert
            AFTER INSERT ON MaintenanceRecords BEGIN
                INSERT INTO MaintenanceSearch(
                    MaintenanceRecordId, MaintenanceNumber, Site, Location, Subject, MaintenanceType,
                    WorkPerformed, PerformedBy, Notes, AssetNumberSnapshot, AssetNameSnapshot, WorkOrderNumberSnapshot)
                VALUES(
                    NEW.MaintenanceRecordId, NEW.MaintenanceNumber, NEW.Site, NEW.Location, NEW.Subject, NEW.MaintenanceType,
                    NEW.WorkPerformed, NEW.PerformedBy, NEW.Notes, NEW.AssetNumberSnapshot, NEW.AssetNameSnapshot, NEW.WorkOrderNumberSnapshot);
            END;

            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceSearch_Update
            AFTER UPDATE OF MaintenanceNumber, Site, Location, Subject, MaintenanceType, WorkPerformed, PerformedBy, Notes,
                            AssetNumberSnapshot, AssetNameSnapshot, WorkOrderNumberSnapshot ON MaintenanceRecords BEGIN
                DELETE FROM MaintenanceSearch WHERE MaintenanceRecordId = OLD.MaintenanceRecordId;
                INSERT INTO MaintenanceSearch(
                    MaintenanceRecordId, MaintenanceNumber, Site, Location, Subject, MaintenanceType,
                    WorkPerformed, PerformedBy, Notes, AssetNumberSnapshot, AssetNameSnapshot, WorkOrderNumberSnapshot)
                VALUES(
                    NEW.MaintenanceRecordId, NEW.MaintenanceNumber, NEW.Site, NEW.Location, NEW.Subject, NEW.MaintenanceType,
                    NEW.WorkPerformed, NEW.PerformedBy, NEW.Notes, NEW.AssetNumberSnapshot, NEW.AssetNameSnapshot, NEW.WorkOrderNumberSnapshot);
            END;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task EnsureAnnualDashboardSummaryInfrastructureAsync(AnnualDbContext context, CancellationToken cancellationToken)
    {
        // DB-7 derived dashboard summaries. MaintenanceRecords remains canonical.
        // Summary rows are intentionally free of constraints that could block canonical
        // Maintenance writes if derived state is damaged. The update trigger removes
        // empty buckets; verification/rebuild detects and repairs mismatches.
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS MaintenanceLocationSummary (
                Label TEXT NOT NULL PRIMARY KEY,
                RecordCount INTEGER NOT NULL,
                TotalCostMinorUnits INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS MaintenanceTypeSummary (
                Label TEXT NOT NULL PRIMARY KEY,
                RecordCount INTEGER NOT NULL,
                TotalCostMinorUnits INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_MaintenanceLocationSummary_RecordCount_Label
            ON MaintenanceLocationSummary (RecordCount DESC, Label COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_MaintenanceTypeSummary_RecordCount_Label
            ON MaintenanceTypeSummary (RecordCount DESC, Label COLLATE NOCASE);

            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceDashboardSummary_Insert
            AFTER INSERT ON MaintenanceRecords
            WHEN NEW.IsInvalid = 0
            BEGIN
                INSERT INTO MaintenanceLocationSummary (Label, RecordCount, TotalCostMinorUnits)
                SELECT NEW.Location, 1, COALESCE(NEW.CostMinorUnits, 0)
                WHERE NEW.Location <> ''
                ON CONFLICT(Label) DO UPDATE SET
                    RecordCount = RecordCount + 1,
                    TotalCostMinorUnits = TotalCostMinorUnits + excluded.TotalCostMinorUnits;

                INSERT INTO MaintenanceTypeSummary (Label, RecordCount, TotalCostMinorUnits)
                SELECT NEW.MaintenanceType, 1, COALESCE(NEW.CostMinorUnits, 0)
                WHERE NEW.MaintenanceType <> ''
                ON CONFLICT(Label) DO UPDATE SET
                    RecordCount = RecordCount + 1,
                    TotalCostMinorUnits = TotalCostMinorUnits + excluded.TotalCostMinorUnits;
            END;

            CREATE TRIGGER IF NOT EXISTS TR_MaintenanceDashboardSummary_Update
            AFTER UPDATE OF Location, MaintenanceType, CostMinorUnits, IsInvalid ON MaintenanceRecords
            BEGIN
                UPDATE MaintenanceLocationSummary
                SET RecordCount = RecordCount - 1,
                    TotalCostMinorUnits = TotalCostMinorUnits - COALESCE(OLD.CostMinorUnits, 0)
                WHERE OLD.IsInvalid = 0 AND OLD.Location <> '' AND Label = OLD.Location;
                DELETE FROM MaintenanceLocationSummary WHERE RecordCount <= 0;

                UPDATE MaintenanceTypeSummary
                SET RecordCount = RecordCount - 1,
                    TotalCostMinorUnits = TotalCostMinorUnits - COALESCE(OLD.CostMinorUnits, 0)
                WHERE OLD.IsInvalid = 0 AND OLD.MaintenanceType <> '' AND Label = OLD.MaintenanceType;
                DELETE FROM MaintenanceTypeSummary WHERE RecordCount <= 0;

                INSERT INTO MaintenanceLocationSummary (Label, RecordCount, TotalCostMinorUnits)
                SELECT NEW.Location, 1, COALESCE(NEW.CostMinorUnits, 0)
                WHERE NEW.IsInvalid = 0 AND NEW.Location <> ''
                ON CONFLICT(Label) DO UPDATE SET
                    RecordCount = RecordCount + 1,
                    TotalCostMinorUnits = TotalCostMinorUnits + excluded.TotalCostMinorUnits;

                INSERT INTO MaintenanceTypeSummary (Label, RecordCount, TotalCostMinorUnits)
                SELECT NEW.MaintenanceType, 1, COALESCE(NEW.CostMinorUnits, 0)
                WHERE NEW.IsInvalid = 0 AND NEW.MaintenanceType <> ''
                ON CONFLICT(Label) DO UPDATE SET
                    RecordCount = RecordCount + 1,
                    TotalCostMinorUnits = TotalCostMinorUnits + excluded.TotalCostMinorUnits;
            END;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireObjectsAsync(DbContext context, string type, IEnumerable<string> names, CancellationToken cancellationToken)
    {
        foreach (var name in names)
        {
            if (!await SchemaObjectExistsAsync(context, type, name, cancellationToken).ConfigureAwait(false))
                throw Incompatible(context.Database.GetDbConnection().DataSource, $"missing required {type} '{name}'");
        }
    }

    private static async Task<bool> TableExistsAsync(DbContext context, string tableName, CancellationToken cancellationToken) =>
        await SchemaObjectExistsAsync(context, "table", tableName, cancellationToken).ConfigureAwait(false);

    private static async Task<bool> SchemaObjectExistsAsync(DbContext context, string type, string name, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        var typeParameter = command.CreateParameter();
        typeParameter.ParameterName = "$type";
        typeParameter.Value = type;
        command.Parameters.Add(typeParameter);
        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = name;
        command.Parameters.Add(nameParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static InvalidOperationException Incompatible(string path, string detail) =>
        new($"Database '{path}' is not the supported General Maintenance Manager production schema ({detail}). DatabaseInitializer never adopts or migrates unknown files. Pre-production development databases may be recreated; production data requires an explicitly supported forward migration with a verified pre-upgrade backup. No migration was attempted.");

    public void Dispose() => _gate.Dispose();
}
