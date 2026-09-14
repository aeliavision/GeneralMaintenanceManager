namespace GeneralMaintenanceManager.Infrastructure.Data;

/// <summary>
/// Central annual-partition router/lifetime boundary. It caches only schema verification
/// through DatabaseInitializer; EF contexts themselves remain short-lived.
/// </summary>
public sealed class AnnualMaintenanceDatabaseManager(
    DataPaths paths,
    DatabaseContextFactory contextFactory,
    DatabaseInitializer initializer)
{
    public IReadOnlyList<int> DiscoverExistingYears() => paths.DiscoverExistingYears();

    public bool Exists(int year)
    {
        DataPaths.ValidateYear(year);
        return File.Exists(paths.GetYearDatabasePath(year));
    }

    public async Task EnsureAsync(int year, CancellationToken cancellationToken = default) =>
        await initializer.EnsureYearAsync(year, cancellationToken).ConfigureAwait(false);

    public async Task<AnnualDbContext> OpenAsync(int year, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(year, cancellationToken).ConfigureAwait(false);
        return contextFactory.CreateAnnualDbContext(year);
    }

    public AnnualDbContext OpenExisting(int year)
    {
        DataPaths.ValidateYear(year);
        if (!Exists(year)) throw new FileNotFoundException($"Annual Maintenance database for {year} was not found.", paths.GetYearDatabasePath(year));
        return contextFactory.CreateAnnualDbContext(year);
    }
}
