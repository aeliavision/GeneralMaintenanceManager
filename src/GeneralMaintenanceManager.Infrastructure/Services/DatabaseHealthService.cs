using System.Globalization;
using System.IO;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class DatabaseHealthService(DataPaths paths) : IDataHealthService
{
    // Startup must remain O(1) with respect to historical corpus size. Validate only
    // connectivity + immutable schema identity for master/current-year databases.
    public async Task ValidateStartupDataAsync(CancellationToken cancellationToken = default)
    {
        await ValidateDatabaseIdentityAsync(
            paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            DatabaseInitializer.MasterSchemaVersion,
            null,
            cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseIdentityAsync(
            paths.GetYearDatabasePath(DateTime.Today.Year),
            DatabaseInitializer.AnnualFormatId,
            DatabaseInitializer.AnnualSchemaVersion,
            DateTime.Today.Year,
            cancellationToken).ConfigureAwait(false);
    }

    // Full semantic/integrity validation is intentionally explicit and may be expensive.
    // It is used by backup verification, Database & Maintenance, stress and certification.
    public async Task ValidateCurrentDataAsync(CancellationToken cancellationToken = default)
    {
        await ValidateDatabaseAsync(
            paths.MasterDatabasePath,
            DatabaseInitializer.MasterFormatId,
            DatabaseInitializer.MasterSchemaVersion,
            null,
            cancellationToken).ConfigureAwait(false);
        foreach (var year in paths.DiscoverExistingYears())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateDatabaseAsync(
                paths.GetYearDatabasePath(year),
                DatabaseInitializer.AnnualFormatId,
                DatabaseInitializer.AnnualSchemaVersion,
                year,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ValidateDatabaseAsync(
        string databasePath,
        string expectedFormatId,
        int expectedSchemaVersion,
        int? expectedPartitionYear,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed for '{databasePath}': {result ?? "unknown error"}.");
        }

        await ValidateIdentityAsync(
            connection,
            databasePath,
            expectedFormatId,
            expectedSchemaVersion,
            expectedPartitionYear,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidateDatabaseIdentityAsync(
        string databasePath,
        string expectedFormatId,
        int expectedSchemaVersion,
        int? expectedPartitionYear,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await ValidateIdentityAsync(
            connection,
            databasePath,
            expectedFormatId,
            expectedSchemaVersion,
            expectedPartitionYear,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("Required SQLite database was not found.", databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ValidateIdentityAsync(
        SqliteConnection connection,
        string databasePath,
        string expectedFormatId,
        int expectedSchemaVersion,
        int? expectedPartitionYear,
        CancellationToken cancellationToken)
    {
        await using var identity = connection.CreateCommand();
        identity.CommandText = "SELECT FormatId, SchemaVersion, PartitionYear FROM SchemaInfo WHERE Id=1;";
        try
        {
            await using var reader = await identity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"Database '{databasePath}' has no clean schema identity.");
            var format = reader.GetString(0);
            var version = reader.GetInt32(1);
            var partitionYear = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            if (!string.Equals(format, expectedFormatId, StringComparison.Ordinal)
                || version != expectedSchemaVersion
                || partitionYear != expectedPartitionYear)
            {
                throw new InvalidDataException(
                    $"Database '{databasePath}' uses incompatible schema identity '{format}' v{version} partition '{partitionYear?.ToString(CultureInfo.InvariantCulture) ?? "master"}'; expected '{expectedFormatId}' v{expectedSchemaVersion} partition '{expectedPartitionYear?.ToString(CultureInfo.InvariantCulture) ?? "master"}'.");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException(
                $"Database '{databasePath}' is not a clean General Maintenance Manager database. No migration was attempted.", ex);
        }
    }
}
