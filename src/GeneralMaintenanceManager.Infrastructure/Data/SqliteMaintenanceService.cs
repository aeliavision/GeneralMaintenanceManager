using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// DB-8 centralized SQLite planner/statistics maintenance. Normal application startup
/// does not call this service. Bulk loaders and certification tooling invoke it explicitly.
/// No automatic VACUUM policy exists; space reclamation remains an operator decision.
/// </summary>
public sealed class SqliteMaintenanceService(DataPaths paths)
{
    public const long OptimizeAfterBulkChangeThreshold = 1_000;
    public const long AnalyzeAfterBulkChangeThreshold = 50_000;

    public Task<SqliteMaintenanceResult> OptimizeMasterAsync(
        long changedRows = 0,
        bool forceAnalyze = false,
        CancellationToken cancellationToken = default) =>
        OptimizeDatabaseAsync(paths.MasterDatabasePath, changedRows, forceAnalyze, cancellationToken);

    public Task<SqliteMaintenanceResult> OptimizeYearAsync(
        int year,
        long changedRows = 0,
        bool forceAnalyze = false,
        CancellationToken cancellationToken = default)
    {
        DataPaths.ValidateYear(year);
        return OptimizeDatabaseAsync(paths.GetYearDatabasePath(year), changedRows, forceAnalyze, cancellationToken);
    }

    public static async Task<SqliteMaintenanceResult> OptimizeDatabaseAsync(
        string databasePath,
        long changedRows = 0,
        bool forceAnalyze = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegative(changedRows);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("SQLite database was not found.", databasePath);

        await using var connection = await OpenReadWriteAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var before = await InspectOpenConnectionAsync(connection, databasePath, cancellationToken).ConfigureAwait(false);
        var analyze = forceAnalyze || changedRows >= AnalyzeAfterBulkChangeThreshold;

        if (analyze)
            await ExecuteNonQueryAsync(connection, "ANALYZE;", cancellationToken).ConfigureAwait(false);

        // PRAGMA optimize is deliberately explicit and infrequent. It is not run by DatabaseInitializer.
        await ExecuteNonQueryAsync(connection, "PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
        var after = await InspectOpenConnectionAsync(connection, databasePath, cancellationToken).ConfigureAwait(false);
        return new SqliteMaintenanceResult(analyze, changedRows, before, after);
    }

    private static async Task<SqliteConnection> OpenReadWriteAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SqliteMaintenanceSnapshot> InspectOpenConnectionAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var pageCount = await ScalarInt64Async(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        var pageSize = checked((int)await ScalarInt64Async(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false));
        var freelistCount = await ScalarInt64Async(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        var schemaVersion = checked((int)await ScalarInt64Async(connection, "PRAGMA schema_version;", cancellationToken).ConfigureAwait(false));
        var journalMode = await ScalarStringAsync(connection, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
        var statisticsRows = await HasObjectAsync(connection, "table", "sqlite_stat1", cancellationToken).ConfigureAwait(false)
            ? await ScalarInt64Async(connection, "SELECT COUNT(*) FROM sqlite_stat1;", cancellationToken).ConfigureAwait(false)
            : 0;
        return new SqliteMaintenanceSnapshot(
            Path.GetFullPath(databasePath),
            pageCount,
            pageSize,
            freelistCount,
            statisticsRows,
            schemaVersion,
            journalMode);
    }

    private static async Task<bool> HasObjectAsync(SqliteConnection connection, string type, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record SqliteMaintenanceSnapshot(
    string DatabasePath,
    long PageCount,
    int PageSize,
    long FreelistCount,
    long StatisticsRowCount,
    int SchemaVersion,
    string JournalMode)
{
    public long ApproximateDatabaseBytes => checked(PageCount * PageSize);
}

public sealed record SqliteMaintenanceResult(
    bool AnalyzeRan,
    long ChangedRows,
    SqliteMaintenanceSnapshot Before,
    SqliteMaintenanceSnapshot After);
