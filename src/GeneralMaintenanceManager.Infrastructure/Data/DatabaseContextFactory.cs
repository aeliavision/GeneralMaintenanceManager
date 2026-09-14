using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using GeneralMaintenanceManager.Infrastructure.Services;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public sealed class DatabaseContextFactory
{
    private readonly DataPaths _paths;
    private readonly DbContextOptions<MasterDbContext> _masterOptions;
    private readonly ConcurrentDictionary<int, DbContextOptions<AnnualDbContext>> _annualOptions = new();
    private readonly DatabaseActivityGate? _databaseActivityGate;

    public DatabaseContextFactory(DataPaths paths, DatabaseActivityGate? databaseActivityGate = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _databaseActivityGate = databaseActivityGate;
        _masterOptions = BuildOptions<MasterDbContext>(paths.MasterDatabasePath);
    }

    public string MasterDatabasePath => _paths.MasterDatabasePath;
    public string BackupsDirectory => _paths.BackupsDirectory;
    public string GetYearDatabasePath(int year) => _paths.GetYearDatabasePath(year);

    public MasterDbContext CreateMasterDbContext() => new(_masterOptions, _databaseActivityGate?.EnterRead());

    public AnnualDbContext CreateAnnualDbContext(int year)
    {
        DataPaths.ValidateYear(year);
        var options = _annualOptions.GetOrAdd(
            year,
            requestedYear => BuildOptions<AnnualDbContext>(_paths.GetYearDatabasePath(requestedYear)));
        return new AnnualDbContext(options, _databaseActivityGate?.EnterRead());
    }

    // Pooling remains deliberately disabled for this portable, single-user desktop app.
    // Caching immutable EF options removes repeated builder/connection-string setup while
    // short-lived DbContext instances still release SQLite files promptly for backup/restore.
    private static DbContextOptions<TContext> BuildOptions<TContext>(string databasePath)
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite(BuildConnectionString(databasePath))
            .AddInterceptors(SqlitePerformanceInterceptor.Instance)
            .EnableDetailedErrors()
            .Options;
    }

    private static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        };

        return builder.ToString();
    }
}
