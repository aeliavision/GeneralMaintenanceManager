using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class MedEquipArchitectureAdoptionTests
{
    [TestMethod]
    public void SingleInstanceGuardAllowsOnlyOneOwnerForPortableDataDirectory()
    {
        using var scope = new TestDataScope();
        using var first = new SingleInstanceGuard(scope.Paths);
        using var second = new SingleInstanceGuard(scope.Paths);

        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(second.TryAcquire());
    }

    [TestMethod]
    public async Task DatabaseActivityGateExclusiveLeaseWaitsForActiveReader()
    {
        using var gate = new DatabaseActivityGate();
        using var reader = await gate.EnterReadAsync().ConfigureAwait(false);
        var exclusiveTask = gate.EnterExclusiveAsync();

        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(exclusiveTask.IsCompleted);

        reader.Dispose();
        using var writer = await exclusiveTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StartupHealthDoesNotOpenArchivedAnnualDatabase()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync().ConfigureAwait(false);

        var archivedYear = DateTime.Today.Year - 3;
        var archivedPath = scope.Paths.GetYearDatabasePath(archivedYear);
        await File.WriteAllTextAsync(archivedPath, "deliberately not a SQLite database").ConfigureAwait(false);

        var health = new DatabaseHealthService(scope.Paths);
        await health.ValidateStartupDataAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SqlitePerformanceInterceptorAppliesProductionConnectionPragmas()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync().ConfigureAwait(false);

        await using var context = factory.CreateMasterDbContext();
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();

        Assert.AreEqual("wal", await ScalarTextAsync(connection, "PRAGMA journal_mode;").ConfigureAwait(false));
        Assert.AreEqual("1", await ScalarTextAsync(connection, "PRAGMA synchronous;").ConfigureAwait(false));
        Assert.AreEqual("2", await ScalarTextAsync(connection, "PRAGMA temp_store;").ConfigureAwait(false));
        Assert.AreEqual("5000", await ScalarTextAsync(connection, "PRAGMA busy_timeout;").ConfigureAwait(false));
        Assert.IsTrue(long.Parse(await ScalarTextAsync(connection, "PRAGMA cache_size;").ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) < 0);
    }

    [TestMethod]
    public void PrintSettingsRoundTripPersistsLayoutAndBranding()
    {
        using var scope = new TestDataScope();
        var service = new UserSettingsService(scope.Paths);
        var settings = PrintSettings.Default with
        {
            AutoPrintWorkOrderActions = true,
            HeaderText = "Facilities Department",
            FooterText = "Internal maintenance record",
            CenterBrandingMode = PrintCenterBrandingMode.Text,
            CenterText = "GMM",
            WorkOrderLayout = PrintSettings.Default.WorkOrderLayout with { ShowStatus = false, ShowSignature = false },
            MaintenanceLayout = PrintSettings.Default.MaintenanceLayout with { ShowAuditInformation = false },
            ListReportLayout = PrintSettings.Default.ListReportLayout with { ShowNotes = false }
        };

        service.SavePrintSettings(settings);
        var restored = service.LoadPrintSettings();

        Assert.AreEqual(settings, restored);
    }

    private static async Task<string> ScalarTextAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
