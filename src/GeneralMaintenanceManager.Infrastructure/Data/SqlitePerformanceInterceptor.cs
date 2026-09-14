using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GeneralMaintenanceManager.Infrastructure.Data;

internal sealed class SqlitePerformanceInterceptor : DbConnectionInterceptor
{
    public static SqlitePerformanceInterceptor Instance { get; } = new();

    private SqlitePerformanceInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Configure((SqliteConnection)connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await ConfigureAsync((SqliteConnection)connection, cancellationToken).ConfigureAwait(false);

    private static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = BuildPragmaSql();
        command.ExecuteNonQuery();
    }

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildPragmaSql();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildPragmaSql()
    {
        var cacheKiB = Environment.Is64BitProcess ? 65_536 : 32_768;
        var mmapBytes = Environment.Is64BitProcess ? 268_435_456L : 67_108_864L;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-{cacheKiB}; PRAGMA mmap_size={mmapBytes};");
    }
}
