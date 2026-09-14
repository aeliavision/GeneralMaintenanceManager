using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public sealed record ProductionMigrationRequest(
    string DatabasePath,
    string ExpectedFormatId,
    int? ExpectedPartitionYear,
    int TargetVersion,
    string PreUpgradeBackupPath);

public sealed record ProductionMigrationBatchResult(bool IsComplete, string? NextCursor);

public sealed record ProductionMigrationStep(
    string MigrationId,
    int FromVersion,
    int ToVersion,
    Func<SqliteConnection, SqliteTransaction, string?, CancellationToken, Task<ProductionMigrationBatchResult>> ApplyBatchAsync,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> ValidateAsync);

public sealed record ProductionMigrationResult(
    int OriginalVersion,
    int FinalVersion,
    IReadOnlyList<string> AppliedMigrationIds);

/// <summary>
/// DB-9 forward-only production migration coordinator. DatabaseInitializer invokes this
/// coordinator only for a recognized GMM production identity whose version has one registered
/// forward path; strict schema validation still runs after migration.
/// </summary>
public sealed class ProductionMigrationCoordinator
{
    private readonly int _maxMigrationBatchesPerStep;

    public ProductionMigrationCoordinator() : this(10_000)
    {
    }

    internal ProductionMigrationCoordinator(int maxMigrationBatchesPerStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMigrationBatchesPerStep);
        _maxMigrationBatchesPerStep = maxMigrationBatchesPerStep;
    }

    public async Task<ProductionMigrationResult> MigrateAsync(
        ProductionMigrationRequest request,
        IReadOnlyCollection<ProductionMigrationStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedFormatId);
        if (request.TargetVersion < ProductionSchemaPolicy.BaselineVersion)
            throw new ArgumentOutOfRangeException(nameof(request), "Target production schema version is below the production baseline.");
        if (!File.Exists(request.DatabasePath)) throw new FileNotFoundException("Database selected for migration was not found.", request.DatabasePath);

        await using var connection = await OpenAsync(request.DatabasePath, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(request, identity);
        var originalVersion = identity.SchemaVersion;

        if (identity.SchemaVersion > request.TargetVersion)
            throw new InvalidDataException($"Database '{request.DatabasePath}' uses newer schema v{identity.SchemaVersion}; this release targets v{request.TargetVersion}. A newer/unknown production schema is never modified automatically.");
        if (identity.SchemaVersion == request.TargetVersion)
            return new ProductionMigrationResult(originalVersion, identity.SchemaVersion, []);
        if (identity.SchemaVersion < ProductionSchemaPolicy.BaselineVersion)
            throw new InvalidDataException($"Database '{request.DatabasePath}' predates the supported GMM production baseline and cannot be adopted automatically.");

        await RequireMigrationInfrastructureAsync(connection, cancellationToken).ConfigureAwait(false);
        await RequireQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);

        var initialCandidates = steps.Where(step => step.FromVersion == identity.SchemaVersion).ToArray();
        if (initialCandidates.Length != 1)
            throw new InvalidOperationException($"Expected exactly one registered forward migration from schema v{identity.SchemaVersion}; found {initialCandidates.Length}.");
        var initialStep = initialCandidates[0];
        ValidateStep(initialStep, identity.SchemaVersion, request.TargetVersion);
        var resumeJournal = await ReadJournalAsync(connection, initialStep.MigrationId, cancellationToken).ConfigureAwait(false);

        var backupPath = Path.GetFullPath(request.PreUpgradeBackupPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(request.PreUpgradeBackupPath) || !File.Exists(backupPath))
            throw new InvalidOperationException("A verified pre-upgrade backup file is required before any production schema migration can begin.");
        await ValidatePreUpgradeBackupAsync(
            connection,
            backupPath,
            request,
            identity,
            requireLiveSnapshotCorrespondence: resumeJournal is null,
            cancellationToken).ConfigureAwait(false);
        var backupSha256 = await ComputeSha256Async(backupPath, cancellationToken).ConfigureAwait(false);
        ValidateExistingJournal(initialStep, resumeJournal, backupSha256);

        var applied = new List<string>();
        var currentVersion = identity.SchemaVersion;
        while (currentVersion < request.TargetVersion)
        {
            var candidates = steps.Where(step => step.FromVersion == currentVersion).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"Expected exactly one registered forward migration from schema v{currentVersion}; found {candidates.Length}.");
            var step = candidates[0];
            ValidateStep(step, currentVersion, request.TargetVersion);

            await RunStepAsync(connection, step, backupPath, backupSha256, cancellationToken).ConfigureAwait(false);
            currentVersion = step.ToVersion;
            applied.Add(step.MigrationId);
        }

        await RequireQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);
        var finalIdentity = await ReadIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
        if (finalIdentity.SchemaVersion != request.TargetVersion)
            throw new InvalidDataException($"Migration ended at schema v{finalIdentity.SchemaVersion}, expected v{request.TargetVersion}.");
        ValidateIdentity(request with { TargetVersion = finalIdentity.SchemaVersion }, finalIdentity);
        return new ProductionMigrationResult(originalVersion, finalIdentity.SchemaVersion, applied);
    }

    private async Task RunStepAsync(
        SqliteConnection connection,
        ProductionMigrationStep step,
        string backupPath,
        string backupSha256,
        CancellationToken cancellationToken)
    {
        var journal = await ReadJournalAsync(connection, step.MigrationId, cancellationToken).ConfigureAwait(false);
        ValidateExistingJournal(step, journal, backupSha256);
        if (string.Equals(journal?.State, "Completed", StringComparison.Ordinal))
        {
            var identity = await ReadIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
            if (identity.SchemaVersion != step.ToVersion)
                throw new InvalidDataException($"Migration journal '{step.MigrationId}' says Completed but SchemaInfo is v{identity.SchemaVersion}, expected v{step.ToVersion}.");
            return;
        }

        var cursor = journal?.ProgressCursor;
        var readyToFinalize = string.Equals(journal?.State, "ReadyToFinalize", StringComparison.Ordinal);
        var batches = 0;
        while (!readyToFinalize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++batches > _maxMigrationBatchesPerStep)
                throw new InvalidOperationException($"Migration '{step.MigrationId}' exceeded the safety batch limit.");

            try
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                await UpsertJournalStartAsync(connection, transaction, step, cursor, backupPath, backupSha256, nowTicks, cancellationToken).ConfigureAwait(false);
                var batch = await step.ApplyBatchAsync(connection, transaction, cursor, cancellationToken).ConfigureAwait(false);
                var nextCursor = batch.NextCursor ?? string.Empty;
                var nextReadyToFinalize = batch.IsComplete;
                if (!nextReadyToFinalize && string.Equals(nextCursor, cursor ?? string.Empty, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Migration '{step.MigrationId}' did not advance its progress cursor. The migration was stopped to prevent an infinite loop.");
                await UpdateJournalProgressAsync(connection, transaction, step.MigrationId, nextCursor,
                    nextReadyToFinalize ? "ReadyToFinalize" : "InProgress", nowTicks, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                // Only expose progress after the transaction that owns that progress committed.
                // A commit failure must never cause recovery to skip uncommitted migration work.
                cursor = nextCursor;
                readyToFinalize = nextReadyToFinalize;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(connection, step, cursor, backupPath, backupSha256, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await step.ValidateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DROP TRIGGER IF EXISTS TR_SchemaInfo_BlockUpdate;", cancellationToken).ConfigureAwait(false);

            await using (var updateIdentity = connection.CreateCommand())
            {
                updateIdentity.Transaction = transaction;
                updateIdentity.CommandText = "UPDATE SchemaInfo SET SchemaVersion=$toVersion WHERE Id=1 AND SchemaVersion=$fromVersion;";
                updateIdentity.Parameters.AddWithValue("$toVersion", step.ToVersion);
                updateIdentity.Parameters.AddWithValue("$fromVersion", step.FromVersion);
                if (await updateIdentity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException($"SchemaInfo did not advance exactly once from v{step.FromVersion} to v{step.ToVersion}.");
            }

            await ExecuteAsync(connection, transaction,
                "CREATE TRIGGER TR_SchemaInfo_BlockUpdate BEFORE UPDATE ON SchemaInfo BEGIN SELECT RAISE(ABORT, 'Database schema identity is immutable.'); END;",
                cancellationToken).ConfigureAwait(false);
            await MarkCompletedAsync(connection, transaction, step.MigrationId, cursor ?? string.Empty, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(connection, step, cursor, backupPath, backupSha256, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateStep(ProductionMigrationStep step, int currentVersion, int targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step.MigrationId);
        ArgumentNullException.ThrowIfNull(step.ApplyBatchAsync);
        ArgumentNullException.ThrowIfNull(step.ValidateAsync);
        if (step.FromVersion != currentVersion || step.ToVersion <= step.FromVersion || step.ToVersion > targetVersion)
            throw new InvalidOperationException($"Migration '{step.MigrationId}' has an invalid version edge {step.FromVersion}->{step.ToVersion} for current v{currentVersion} / target v{targetVersion}.");
    }

    private static void ValidateIdentity(ProductionMigrationRequest request, SchemaIdentity identity)
    {
        if (!string.Equals(identity.FormatId, request.ExpectedFormatId, StringComparison.Ordinal)
            || identity.PartitionYear != request.ExpectedPartitionYear)
        {
            throw new InvalidDataException($"Database identity mismatch. Found '{identity.FormatId}' partition '{identity.PartitionYear?.ToString(CultureInfo.InvariantCulture) ?? "master"}', expected '{request.ExpectedFormatId}' partition '{request.ExpectedPartitionYear?.ToString(CultureInfo.InvariantCulture) ?? "master"}'. Unknown database files are never adopted.");
        }
    }

    private static void ValidateExistingJournal(ProductionMigrationStep step, MigrationJournalState? journal, string backupSha256)
    {
        if (journal is null) return;
        if (journal.FromVersion != step.FromVersion || journal.ToVersion != step.ToVersion)
            throw new InvalidDataException($"Migration journal '{step.MigrationId}' does not match the registered version edge.");
        if (!string.Equals(journal.PreUpgradeBackupSha256, backupSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Migration '{step.MigrationId}' was started with a different pre-upgrade backup. Restore that backup or continue with the original verified backup.");
    }

    private static async Task ValidatePreUpgradeBackupAsync(
        SqliteConnection liveConnection,
        string backupPath,
        ProductionMigrationRequest request,
        SchemaIdentity liveIdentity,
        bool requireLiveSnapshotCorrespondence,
        CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(request.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pre-upgrade backup must be a separate database file, not the live database itself.");

        await using (var backup = await OpenReadOnlyAsync(backupPath, cancellationToken).ConfigureAwait(false))
        {
            var backupIdentity = await ReadIdentityAsync(backup, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(backupIdentity.FormatId, liveIdentity.FormatId, StringComparison.Ordinal)
                || backupIdentity.SchemaVersion != liveIdentity.SchemaVersion
                || backupIdentity.PartitionYear != liveIdentity.PartitionYear)
            {
                throw new InvalidDataException("The pre-upgrade backup does not match the live GMM database identity and schema version.");
            }
            ValidateIdentity(request with { TargetVersion = liveIdentity.SchemaVersion }, backupIdentity);
            await RequireQuickCheckAsync(backup, cancellationToken).ConfigureAwait(false);
        }

        if (!requireLiveSnapshotCorrespondence)
            return;

        // Identity/version equality is not enough: another GMM database can have the same schema.
        // On the first migration attempt, compare the supplied backup with a fresh SQLite backup of
        // the live database before any migration transaction runs. On a restart after committed
        // batches, the live database is intentionally different, so the journal-pinned backup hash
        // is the authority instead and this live-snapshot comparison must not be repeated.
        var verificationPath = Path.Combine(Path.GetTempPath(), $"gmm-migration-backup-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using (var verification = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = verificationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                await verification.OpenAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                liveConnection.BackupDatabase(verification);
            }

            var expectedHash = await ComputeSha256Async(verificationPath, cancellationToken).ConfigureAwait(false);
            var suppliedHash = await ComputeSha256Async(backupPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, suppliedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pre-upgrade backup does not correspond to the current live GMM database snapshot.");
        }
        finally
        {
            try { if (File.Exists(verificationPath)) File.Delete(verificationPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RequireMigrationInfrastructureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await HasObjectAsync(connection, "table", "SchemaMigrationJournal", cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Production migration journal is missing. The database is not a supported GMM production baseline.");
    }

    private static async Task<SchemaIdentity> ReadIdentityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FormatId, SchemaVersion, PartitionYear FROM SchemaInfo WHERE Id=1;";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("SchemaInfo identity row is missing.");
            return new SchemaIdentity(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("Database does not contain a readable GMM SchemaInfo identity.", ex);
        }
    }

    private static async Task<MigrationJournalState?> ReadJournalAsync(SqliteConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FromVersion, ToVersion, State, ProgressCursor, PreUpgradeBackupPath, PreUpgradeBackupSha256 FROM SchemaMigrationJournal WHERE MigrationId=$id;";
        command.Parameters.AddWithValue("$id", migrationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new MigrationJournalState(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    }

    private static async Task UpsertJournalStartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionMigrationStep step,
        string? cursor,
        string backupPath,
        string backupSha256,
        long nowTicks,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO SchemaMigrationJournal (MigrationId, FromVersion, ToVersion, State, ProgressCursor, PreUpgradeBackupPath, PreUpgradeBackupSha256, StartedAtUtcTicks, UpdatedAtUtcTicks, CompletedAtUtcTicks, LastError) " +
            "VALUES ($id, $from, $to, 'Started', $cursor, $backupPath, $backupHash, $now, $now, NULL, '') " +
            "ON CONFLICT(MigrationId) DO UPDATE SET State='Started', UpdatedAtUtcTicks=excluded.UpdatedAtUtcTicks, LastError='';";
        command.Parameters.AddWithValue("$id", step.MigrationId);
        command.Parameters.AddWithValue("$from", step.FromVersion);
        command.Parameters.AddWithValue("$to", step.ToVersion);
        command.Parameters.AddWithValue("$cursor", cursor ?? string.Empty);
        command.Parameters.AddWithValue("$backupPath", backupPath);
        command.Parameters.AddWithValue("$backupHash", backupSha256);
        command.Parameters.AddWithValue("$now", nowTicks);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateJournalProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string migrationId,
        string cursor,
        string state,
        long nowTicks,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE SchemaMigrationJournal SET State=$state, ProgressCursor=$cursor, UpdatedAtUtcTicks=$now, LastError='' WHERE MigrationId=$id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$now", nowTicks);
        command.Parameters.AddWithValue("$id", migrationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException($"Migration journal '{migrationId}' could not be updated.");
    }

    private static async Task MarkCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string migrationId,
        string cursor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE SchemaMigrationJournal SET State='Completed', ProgressCursor=$cursor, UpdatedAtUtcTicks=$now, CompletedAtUtcTicks=$now, LastError='' WHERE MigrationId=$id;";
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", migrationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException($"Migration journal '{migrationId}' could not be finalized.");
    }

    private static async Task RecordFailureAsync(
        SqliteConnection connection,
        ProductionMigrationStep step,
        string? cursor,
        string backupPath,
        string backupSha256,
        Exception error,
        CancellationToken cancellationToken)
    {
        var message = error.ToString();
        if (message.Length > 4000) message = message[..4000];
        var now = DateTimeOffset.UtcNow.UtcTicks;
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO SchemaMigrationJournal (MigrationId, FromVersion, ToVersion, State, ProgressCursor, PreUpgradeBackupPath, PreUpgradeBackupSha256, StartedAtUtcTicks, UpdatedAtUtcTicks, CompletedAtUtcTicks, LastError) " +
            "VALUES ($id, $from, $to, 'Failed', $cursor, $backupPath, $backupHash, $now, $now, NULL, $error) " +
            "ON CONFLICT(MigrationId) DO UPDATE SET State='Failed', ProgressCursor=$cursor, UpdatedAtUtcTicks=$now, LastError=$error;";
        command.Parameters.AddWithValue("$id", step.MigrationId);
        command.Parameters.AddWithValue("$from", step.FromVersion);
        command.Parameters.AddWithValue("$to", step.ToVersion);
        command.Parameters.AddWithValue("$cursor", cursor ?? string.Empty);
        command.Parameters.AddWithValue("$backupPath", backupPath);
        command.Parameters.AddWithValue("$backupHash", backupSha256);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$error", message);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireQuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite quick_check failed before/after production migration: {result ?? "unknown error"}.");
    }

    private static async Task<bool> HasObjectAsync(SqliteConnection connection, string type, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed record SchemaIdentity(string FormatId, int SchemaVersion, int? PartitionYear);
    private sealed record MigrationJournalState(
        int FromVersion,
        int ToVersion,
        string State,
        string ProgressCursor,
        string PreUpgradeBackupPath,
        string PreUpgradeBackupSha256);
}
