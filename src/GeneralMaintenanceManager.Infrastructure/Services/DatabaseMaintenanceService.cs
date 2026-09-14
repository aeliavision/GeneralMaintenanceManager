using System.Globalization;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class DatabaseMaintenanceService(
    DataPaths paths,
    DatabaseContextFactory contextFactory,
    IDataHealthService healthService) : IDatabaseMaintenanceService
{
    public async Task<DatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var master = contextFactory.CreateMasterDbContext();
        var pendingCompletions = await master.WorkOrderCompletionIntents.AsNoTracking()
            .CountAsync(item => item.State != WorkOrderCompletionIntentStates.Finalized, cancellationToken)
            .ConfigureAwait(false);
        var workOrderRows = await master.WorkOrders.AsNoTracking().LongCountAsync(cancellationToken).ConfigureAwait(false);
        var workOrderSearchRows = await ScalarLongAsync(master, "SELECT COUNT(*) FROM WorkOrderSearch_docsize;", cancellationToken).ConfigureAwait(false);
        var preventiveOccurrenceRows = await ScalarLongAsync(master, "SELECT COUNT(*) FROM PreventiveDueOccurrence;", cancellationToken).ConfigureAwait(false);
        var journalMode = await ReadPragmaTextAsync(master, "journal_mode", cancellationToken).ConfigureAwait(false);
        var synchronous = await ReadPragmaTextAsync(master, "synchronous", cancellationToken).ConfigureAwait(false);

        long databaseBytes = 0;
        long walBytes = 0;
        foreach (var databasePath in EnumerateDatabasePaths())
        {
            if (File.Exists(databasePath)) databaseBytes += new FileInfo(databasePath).Length;
            var walPath = databasePath + "-wal";
            if (File.Exists(walPath)) walBytes += new FileInfo(walPath).Length;
        }

        return new DatabaseMaintenanceStatus(
            DatabaseInitializer.MasterSchemaVersion,
            DatabaseInitializer.AnnualSchemaVersion,
            paths.DiscoverExistingYears().Count,
            journalMode.ToUpperInvariant(),
            FormatSynchronous(synchronous),
            databaseBytes,
            walBytes,
            pendingCompletions,
            workOrderSearchRows,
            workOrderRows,
            preventiveOccurrenceRows);
    }

    public Task OptimizeAsync(CancellationToken cancellationToken = default) =>
        RunAcrossDatabasesAsync(analyze: false, cancellationToken);

    public Task AnalyzeAndOptimizeAsync(CancellationToken cancellationToken = default) =>
        RunAcrossDatabasesAsync(analyze: true, cancellationToken);

    public Task FullIntegrityCheckAsync(CancellationToken cancellationToken = default) =>
        healthService.ValidateCurrentDataAsync(cancellationToken);

    public async Task RebuildWorkOrderSearchAsync(CancellationToken cancellationToken = default)
    {
        await using var master = contextFactory.CreateMasterDbContext();
        await master.Database.ExecuteSqlRawAsync(
            "INSERT INTO WorkOrderSearch(WorkOrderSearch) VALUES('rebuild');",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RebuildActivityHistorySearchAsync(CancellationToken cancellationToken = default)
    {
        await using var master = contextFactory.CreateMasterDbContext();
        await using var transaction = await master.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await master.Database.ExecuteSqlRawAsync(
            "INSERT INTO ActivityHistorySearch(ActivityHistorySearch) VALUES('rebuild');",
            cancellationToken).ConfigureAwait(false);
        await master.Database.ExecuteSqlRawAsync(
            "UPDATE SearchIndexState SET IsReady=1 WHERE SearchName='ActivityHistorySearch';",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAcrossDatabasesAsync(bool analyze, CancellationToken cancellationToken)
    {
        await using (var master = contextFactory.CreateMasterDbContext())
        {
            await RunMaintenanceAsync(master, analyze, cancellationToken).ConfigureAwait(false);
        }

        foreach (var year in paths.DiscoverExistingYears())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var annual = contextFactory.CreateAnnualDbContext(year);
            await RunMaintenanceAsync(annual, analyze, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunMaintenanceAsync(DbContext context, bool analyze, CancellationToken cancellationToken)
    {
        if (analyze) await context.Database.ExecuteSqlRawAsync("ANALYZE;", cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<string> EnumerateDatabasePaths()
    {
        yield return paths.MasterDatabasePath;
        foreach (var year in paths.DiscoverExistingYears()) yield return paths.GetYearDatabasePath(year);
    }

    private static async Task<string> ReadPragmaTextAsync(DbContext context, string pragma, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma switch
            {
                "journal_mode" => "PRAGMA journal_mode;",
                "synchronous" => "PRAGMA synchronous;",
                _ => throw new ArgumentOutOfRangeException(nameof(pragma), pragma, "Unsupported SQLite PRAGMA.")
            };
            return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            if (openedHere) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<long> ScalarLongAsync(DbContext context, string sql, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string FormatSynchronous(string value) => value switch
    {
        "0" => "OFF",
        "1" => "NORMAL",
        "2" => "FULL",
        "3" => "EXTRA",
        _ => value.ToUpperInvariant()
    };
}
