using System.Globalization;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class DatabaseHardeningCompletionTests
{
    private static readonly string[] ExpectedProbeMigrationIds = ["GMM.TEST.V1_TO_V2"];

    [TestMethod]
    public async Task ProductionBaselineIsVersionOneAndIncludesMigrationJournal()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using var master = await OpenAsync(scope.Paths.MasterDatabasePath);
        await using var annual = await OpenAsync(scope.Paths.GetYearDatabasePath(DateTime.Today.Year));

        var masterSchemaVersion = await ScalarIntAsync(master, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;");
        var annualSchemaVersion = await ScalarIntAsync(annual, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;");
        Assert.AreEqual(ProductionSchemaPolicy.CurrentVersion, masterSchemaVersion);
        Assert.AreEqual(ProductionSchemaPolicy.CurrentVersion, annualSchemaVersion);
        Assert.AreEqual(DatabaseInitializer.MasterSchemaVersion, masterSchemaVersion);
        Assert.AreEqual(DatabaseInitializer.AnnualSchemaVersion, annualSchemaVersion);
        Assert.AreEqual(1, await ObjectCountAsync(master, "table", "SchemaMigrationJournal"));
        Assert.AreEqual(1, await ObjectCountAsync(annual, "table", "SchemaMigrationJournal"));
        Assert.AreEqual(1, await ObjectCountAsync(master, "trigger", "TR_SchemaMigrationJournal_BlockDelete"));
        Assert.AreEqual(1, await ObjectCountAsync(annual, "trigger", "TR_SchemaMigrationJournal_BlockDelete"));
    }

    [TestMethod]
    public async Task BulkMaintenanceRunsAnalyzeAndOptimizeAndCapturesPlannerStatistics()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var service = new SqliteMaintenanceService(scope.Paths);
        var result = await service.OptimizeYearAsync(DateTime.Today.Year, changedRows: 1_000_000, forceAnalyze: true);

        Assert.IsTrue(result.AnalyzeRan);
        Assert.IsTrue(result.After.PageCount > 0);
        Assert.IsTrue(result.After.PageSize >= 512);
        Assert.IsTrue(result.After.StatisticsRowCount > 0);
        Assert.AreEqual("wal", result.After.JournalMode.ToLowerInvariant());
    }

    [TestMethod]
    public async Task ForwardMigrationRequiresPreUpgradeBackupAndRefusesNewerSchema()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var coordinator = new ProductionMigrationCoordinator();
        var requestWithoutBackup = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: Path.Combine(scope.Paths.DataDirectory, "missing.zip"));

        var backupEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.MigrateAsync(requestWithoutBackup, [CreateTwoBatchProbeMigration()]));
        StringAssert.Contains(backupEx.Message, "pre-upgrade backup");

        await using (var connection = await OpenAsync(scope.Paths.MasterDatabasePath))
        {
            await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS TR_SchemaInfo_BlockUpdate;");
            await ExecuteAsync(connection, "UPDATE SchemaInfo SET SchemaVersion=3 WHERE Id=1;");
            await ExecuteAsync(connection,
                "CREATE TRIGGER TR_SchemaInfo_BlockUpdate BEFORE UPDATE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is immutable.'); END;");
        }

        var newerRequest = requestWithoutBackup with { PreUpgradeBackupPath = Path.Combine(scope.Paths.DataDirectory, "not-needed-for-newer-schema.db") };
        var futureEx = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            coordinator.MigrateAsync(newerRequest, [CreateTwoBatchProbeMigration()]));
        StringAssert.Contains(futureEx.Message, "newer schema");
    }

    [TestMethod]
    public async Task SyntheticV1ToV2MigrationIsBatchedJournaledAndRestartSafe()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var backupPath = Path.Combine(scope.Paths.DataDirectory, "pre-upgrade.db");
        await CreateDatabaseBackupAsync(scope.Paths.MasterDatabasePath, backupPath);
        var request = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: backupPath);

        var coordinator = new ProductionMigrationCoordinator();
        var result = await coordinator.MigrateAsync(request, [CreateTwoBatchProbeMigration()]);

        Assert.AreEqual(1, result.OriginalVersion);
        Assert.AreEqual(2, result.FinalVersion);
        CollectionAssert.AreEqual(ExpectedProbeMigrationIds, result.AppliedMigrationIds.ToArray());

        await using var connection = await OpenAsync(scope.Paths.MasterDatabasePath);
        Assert.AreEqual(2, await ScalarIntAsync(connection, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;"));
        Assert.AreEqual(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM MigrationProbe;"));
        Assert.AreEqual("Completed", await ScalarStringAsync(connection,
            "SELECT State FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.V1_TO_V2';"));
        Assert.AreEqual("2", await ScalarStringAsync(connection,
            "SELECT ProgressCursor FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.V1_TO_V2';"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(await ScalarStringAsync(connection,
            "SELECT PreUpgradeBackupSha256 FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.V1_TO_V2';")));
    }

    [TestMethod]
    public async Task CommitFailureDoesNotAdvanceMigrationCursorPastCommittedWork()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var backupPath = Path.Combine(scope.Paths.DataDirectory, "pre-upgrade.db");
        await CreateDatabaseBackupAsync(scope.Paths.MasterDatabasePath, backupPath);
        var request = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: backupPath);
        var coordinator = new ProductionMigrationCoordinator();

        var commitFailure = new ProductionMigrationStep(
            "GMM.TEST.COMMIT_FAILURE_CURSOR",
            1,
            2,
            async (connection, transaction, cursor, cancellationToken) =>
            {
                await ExecuteAsync(connection, transaction,
                    "CREATE TABLE MigrationParent (Id INTEGER NOT NULL PRIMARY KEY);", cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "CREATE TABLE MigrationChild (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NOT NULL, " +
                    "FOREIGN KEY (ParentId) REFERENCES MigrationParent(Id) DEFERRABLE INITIALLY DEFERRED);", cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO MigrationChild (Id, ParentId) VALUES (1, 999);", cancellationToken);
                return new ProductionMigrationBatchResult(false, "uncommitted-next-cursor");
            },
            static (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<SqliteException>(() => coordinator.MigrateAsync(request, [commitFailure]));

        await using var connection = await OpenAsync(scope.Paths.MasterDatabasePath);
        Assert.AreEqual(1, await ScalarIntAsync(connection, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;"));
        Assert.AreEqual(string.Empty, await ScalarStringAsync(connection,
            "SELECT ProgressCursor FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.COMMIT_FAILURE_CURSOR';"));
        Assert.AreEqual("Failed", await ScalarStringAsync(connection,
            "SELECT State FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.COMMIT_FAILURE_CURSOR';"));
    }

    [TestMethod]
    public async Task FailedMigrationBatchLeavesProductionVersionUnchangedAndCanBeRetried()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var backupPath = Path.Combine(scope.Paths.DataDirectory, "pre-upgrade.db");
        await CreateDatabaseBackupAsync(scope.Paths.MasterDatabasePath, backupPath);
        var request = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: backupPath);
        var coordinator = new ProductionMigrationCoordinator();

        var failing = new ProductionMigrationStep(
            "GMM.TEST.FAIL_THEN_RETRY",
            1,
            2,
            async (connection, transaction, cursor, cancellationToken) =>
            {
                await ExecuteAsync(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS MigrationRetryProbe (Id INTEGER NOT NULL PRIMARY KEY);", cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO MigrationRetryProbe (Id) VALUES (1);", cancellationToken);
                throw new InvalidOperationException("synthetic migration interruption");
            },
            static (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.MigrateAsync(request, [failing]));

        await using (var connection = await OpenAsync(scope.Paths.MasterDatabasePath))
        {
            Assert.AreEqual(1, await ScalarIntAsync(connection, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;"));
            Assert.AreEqual(0, await ObjectCountAsync(connection, "table", "MigrationRetryProbe"));
            Assert.AreEqual("Failed", await ScalarStringAsync(connection,
                "SELECT State FROM SchemaMigrationJournal WHERE MigrationId='GMM.TEST.FAIL_THEN_RETRY';"));
        }

        var succeeding = new ProductionMigrationStep(
            "GMM.TEST.FAIL_THEN_RETRY",
            1,
            2,
            async (connection, transaction, cursor, cancellationToken) =>
            {
                await ExecuteAsync(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS MigrationRetryProbe (Id INTEGER NOT NULL PRIMARY KEY);", cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO MigrationRetryProbe (Id) VALUES (1);", cancellationToken);
                return new ProductionMigrationBatchResult(true, "done");
            },
            async (connection, transaction, cancellationToken) =>
            {
                var count = await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM MigrationRetryProbe;", cancellationToken);
                if (count != 1) throw new InvalidOperationException("Migration retry probe validation failed.");
            });

        var recovered = await coordinator.MigrateAsync(request, [succeeding]);
        Assert.AreEqual(2, recovered.FinalVersion);
    }

    [TestMethod]
    public async Task MigrationRejectsUnrelatedBackupEvenWhenItIsReadableSqlite()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var unrelated = Path.Combine(scope.Paths.DataDirectory, "unrelated.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = unrelated,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE Anything (Id INTEGER PRIMARY KEY);");
        }

        var request = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: unrelated);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new ProductionMigrationCoordinator().MigrateAsync(request, [CreateTwoBatchProbeMigration()]));
    }

    [TestMethod]
    public async Task MigrationRejectsDifferentGmmDatabaseAsCorrespondingBackup()
    {
        using var liveScope = new TestDataScope();
        var liveFactory = new DatabaseContextFactory(liveScope.Paths);
        using var liveInitializer = new DatabaseInitializer(liveFactory);
        await liveInitializer.InitializeAsync();

        using var otherScope = new TestDataScope();
        var otherFactory = new DatabaseContextFactory(otherScope.Paths);
        using var otherInitializer = new DatabaseInitializer(otherFactory);
        await otherInitializer.InitializeAsync();

        await using (var otherContext = otherFactory.CreateMasterDbContext())
        {
            otherContext.Asset.Add(new Asset
            {
                AssetId = Guid.NewGuid(),
                AssetName = "Different database marker",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await otherContext.SaveChangesAsync();
        }

        var request = new ProductionMigrationRequest(
            liveScope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: otherScope.Paths.MasterDatabasePath);

        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new ProductionMigrationCoordinator().MigrateAsync(request, [CreateTwoBatchProbeMigration()]));
        StringAssert.Contains(ex.Message, "does not correspond");
    }

    [TestMethod]
    public async Task MigrationStopsImmediatelyWhenBatchCursorDoesNotAdvance()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var backupPath = Path.Combine(scope.Paths.DataDirectory, "pre-upgrade.db");
        await CreateDatabaseBackupAsync(scope.Paths.MasterDatabasePath, backupPath);

        var stuck = new ProductionMigrationStep(
            "GMM.TEST.NON_PROGRESS",
            1,
            2,
            static (_, _, cursor, _) => Task.FromResult(new ProductionMigrationBatchResult(false, cursor ?? string.Empty)),
            static (_, _, _) => Task.CompletedTask);
        var request = new ProductionMigrationRequest(
            scope.Paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            ExpectedPartitionYear: null,
            TargetVersion: 2,
            PreUpgradeBackupPath: backupPath);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ProductionMigrationCoordinator().MigrateAsync(request, [stuck]));
        StringAssert.Contains(ex.Message, "did not advance");
    }

    private static ProductionMigrationStep CreateTwoBatchProbeMigration() =>
        new(
            "GMM.TEST.V1_TO_V2",
            1,
            2,
            async (connection, transaction, cursor, cancellationToken) =>
            {
                await ExecuteAsync(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS MigrationProbe (Id INTEGER NOT NULL PRIMARY KEY, Value TEXT NOT NULL);", cancellationToken);
                var completedBatches = string.IsNullOrWhiteSpace(cursor)
                    ? 0
                    : int.Parse(cursor, CultureInfo.InvariantCulture);
                var next = completedBatches + 1;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO MigrationProbe (Id, Value) VALUES ($id, $value);";
                command.Parameters.AddWithValue("$id", next);
                command.Parameters.AddWithValue("$value", $"batch-{next}");
                await command.ExecuteNonQueryAsync(cancellationToken);
                return new ProductionMigrationBatchResult(next >= 2, next.ToString(CultureInfo.InvariantCulture));
            },
            async (connection, transaction, cancellationToken) =>
            {
                var count = await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM MigrationProbe;", cancellationToken);
                if (count != 2) throw new InvalidOperationException($"Expected 2 migration probe rows, found {count}.");
            });

    private static async Task CreateDatabaseBackupAsync(string sourcePath, string backupPath)
    {
        await using var source = await OpenAsync(sourcePath);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ObjectCountAsync(SqliteConnection connection, string type, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
