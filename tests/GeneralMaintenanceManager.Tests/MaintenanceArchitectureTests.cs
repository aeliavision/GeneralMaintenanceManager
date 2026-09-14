using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class MaintenanceArchitectureTests
{
    [TestMethod]
    public async Task FreshInitializationCreatesCleanMasterAndCurrentAnnualDatabase()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitializer(scope, out _);
        await initializer.InitializeAsync();
        Assert.IsTrue(File.Exists(scope.Paths.MasterDatabasePath));
        Assert.IsTrue(File.Exists(scope.Paths.GetYearDatabasePath(DateTime.Today.Year)));
        StringAssert.EndsWith(scope.Paths.GetYearDatabasePath(DateTime.Today.Year), Path.Combine("Years", $"Maintenance_{DateTime.Today.Year}.db"));
    }

    [TestMethod]
    public async Task MasterDoesNotContainCanonicalMaintenanceRows()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitializer(scope, out _);
        await initializer.InitializeAsync();
        await using var connection = await OpenAsync(scope.Paths.MasterDatabasePath);
        Assert.AreEqual(0L, await ObjectCountAsync(connection, "table", "MaintenanceRecords"));
        Assert.AreEqual(0L, await ObjectCountAsync(connection, "table", "MaintenanceEvents"));
        Assert.AreEqual(0L, await ObjectCountAsync(connection, "table", "MachineEvents"));
    }

    [TestMethod]
    public async Task AnnualDatabaseContainsCanonicalMaintenanceAndActivityTables()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitializer(scope, out _);
        await initializer.InitializeAsync();
        await using var connection = await OpenAsync(scope.Paths.GetYearDatabasePath(DateTime.Today.Year));
        Assert.AreEqual(1L, await ObjectCountAsync(connection, "table", "MaintenanceRecords"));
        Assert.AreEqual(1L, await ObjectCountAsync(connection, "table", "MaintenanceActivity"));
        Assert.AreEqual(0L, await ObjectCountAsync(connection, "table", "MaintenanceEventAudit"));
    }

    [TestMethod]
    public async Task AvailableYearsReflectAnnualPartitionsAndAlwaysIncludeCurrentYearNewestFirst()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var archivedYear = DateTime.Today.Year - 4;
        var archivedOccurrence = new DateTimeOffset(
            new DateTime(archivedYear, 6, 15, 10, 0, 0, DateTimeKind.Local));

        await service.CreateAsync(Draft(archivedOccurrence, assetId: null));

        var years = await service.GetAvailableYearsAsync();

        Assert.IsTrue(years.Contains(DateTime.Today.Year));
        Assert.IsTrue(years.Contains(archivedYear));
        CollectionAssert.AreEqual(
            years.OrderByDescending(year => year).ToArray(),
            years.ToArray());
    }

    [TestMethod]
    public async Task DirectMaintenanceIsAssetOptionalAndUsesSevenDigitReference()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null));
        StringAssert.Matches(record.MaintenanceNumber, new System.Text.RegularExpressions.Regex($"^MNT-{DateTime.Today.Year:0000}-[0-9]{{7}}$"));
        Assert.IsNull(record.AssetId);
        Assert.AreEqual(record.OccurredAt.Year, record.ReferenceYear);
        Assert.IsTrue(File.Exists(scope.Paths.GetYearDatabasePath(record.ReferenceYear)));
    }

    [TestMethod]
    public async Task BackdatedMaintenanceRoutesToItsReferenceYearOnly()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var occurred = new DateTimeOffset(new DateTime(DateTime.Today.Year - 2, 3, 10, 10, 0, 0, DateTimeKind.Local));
        var record = await service.CreateAsync(Draft(occurred, assetId: null));
        Assert.AreEqual(occurred.Year, record.ReferenceYear);
        StringAssert.StartsWith(record.MaintenanceNumber, $"MNT-{occurred.Year:0000}-");
        Assert.IsTrue(File.Exists(scope.Paths.GetYearDatabasePath(occurred.Year)));
        var found = await service.GetByNumberAsync(record.MaintenanceNumber);
        Assert.IsNotNull(found);
        Assert.AreEqual(record.MaintenanceRecordId, found.MaintenanceRecordId);
    }

    [TestMethod]
    public async Task NoOpEditDoesNotCreateRevisionOrActivity()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null));
        var before = await service.GetActivityAsync(record.MaintenanceNumber);
        var edited = await service.UpdateAsync(record.MaintenanceRecordId, record.ReferenceYear, new MaintenanceRecordEditDraft(
            record.OccurredAt, record.Site, record.Location, record.Subject, record.MaintenanceType,
            record.WorkPerformed, record.PerformedBy, record.Cost, record.Notes));
        var after = await service.GetActivityAsync(record.MaintenanceNumber);
        Assert.AreEqual(1, edited.Revision);
        Assert.AreEqual(before.Count, after.Count);
    }

    [TestMethod]
    public async Task CorrectionCannotMovePermanentReferenceToDifferentYear()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null));
        var otherYear = record.OccurredAt.AddYears(-1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.UpdateAsync(
            record.MaintenanceRecordId, record.ReferenceYear,
            new MaintenanceRecordEditDraft(otherYear, record.Site, record.Location, record.Subject, record.MaintenanceType,
                record.WorkPerformed, record.PerformedBy, record.Cost, record.Notes)));
    }

    [TestMethod]
    public async Task MarkInvalidPreservesRecordAndExcludesItFromNormalQuery()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null));
        var invalid = await service.MarkInvalidAsync(record.MaintenanceRecordId, record.ReferenceYear, "Duplicate entry");
        Assert.IsTrue(invalid.IsInvalid);
        Assert.AreEqual("Duplicate entry", invalid.InvalidReason);
        Assert.IsNotNull(await service.GetByNumberAsync(record.MaintenanceNumber));
        var normal = await service.GetPageAsync(new MaintenanceQuery(Year: record.ReferenceYear, IncludeInvalid: false, PageSize: 200));
        Assert.IsFalse(normal.Items.Any(item => item.MaintenanceRecordId == record.MaintenanceRecordId));
    }

    [TestMethod]
    public async Task QueryPageIsCappedAtTwoHundredRows()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        for (var i = 0; i < 205; i++) await service.CreateAsync(Draft(DateTimeOffset.Now.AddMinutes(-i), assetId: null));
        var page = await service.GetPageAsync(new MaintenanceQuery(Year: DateTime.Today.Year, PageSize: 1000));
        Assert.AreEqual(200, page.Items.Count);
        Assert.IsTrue(page.HasMore);
    }


    [TestMethod]
    public async Task MaintenanceSequenceResetsForEachReferenceYear()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var thisYear = DateTime.Today.Year;
        var previousYear = thisYear - 1;

        var current = await service.CreateAsync(Draft(new DateTimeOffset(new DateTime(thisYear, 2, 1, 9, 0, 0, DateTimeKind.Local)), assetId: null));
        var previous = await service.CreateAsync(Draft(new DateTimeOffset(new DateTime(previousYear, 2, 1, 9, 0, 0, DateTimeKind.Local)), assetId: null));

        Assert.AreEqual($"MNT-{thisYear:0000}-0000001", current.MaintenanceNumber);
        Assert.AreEqual($"MNT-{previousYear:0000}-0000001", previous.MaintenanceNumber);
    }

    [TestMethod]
    public async Task ConcurrentMaintenanceCreationDoesNotDuplicatePermanentReferences()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var occurredAt = DateTimeOffset.Now;
        var created = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => service.CreateAsync(Draft(occurredAt.AddSeconds(index), assetId: null))));

        Assert.AreEqual(created.Length, created.Select(item => item.MaintenanceNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(created.All(item => item.ReferenceYear == occurredAt.Year));
    }

    [TestMethod]
    public async Task SpecificMntLookupDoesNotOpenUnrelatedAnnualDatabase()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null));
        var unrelatedYear = record.ReferenceYear - 5;
        var unrelatedPath = scope.Paths.GetYearDatabasePath(unrelatedYear);
        await File.WriteAllBytesAsync(unrelatedPath, [1, 2, 3, 4]);

        var found = await service.GetByNumberAsync(record.MaintenanceNumber);

        Assert.IsNotNull(found);
        Assert.AreEqual(record.MaintenanceRecordId, found.MaintenanceRecordId);
    }

    [TestMethod]
    public async Task AnnualPagingCursorReturnsNextWindowWithoutOverlap()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var occurredAt = DateTimeOffset.Now;
        for (var i = 0; i < 205; i++)
            await service.CreateAsync(Draft(occurredAt.AddMinutes(-i), assetId: null));

        var first = await service.GetPageAsync(new MaintenanceQuery(Year: occurredAt.Year, PageSize: 100));
        Assert.IsTrue(first.HasMore);
        Assert.IsNotNull(first.NextCursor);

        var second = await service.GetPageAsync(new MaintenanceQuery(Year: occurredAt.Year, PageSize: 100, Cursor: first.NextCursor));
        Assert.AreEqual(100, second.Items.Count);
        Assert.AreEqual(0, first.Items.Select(item => item.MaintenanceRecordId).Intersect(second.Items.Select(item => item.MaintenanceRecordId)).Count());
    }

    [TestMethod]
    public async Task AnnualDatabaseContainsDashboardSummaryTablesAndSynchronizationTriggers()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitializer(scope, out _);
        await initializer.InitializeAsync();
        await using var connection = await OpenAsync(scope.Paths.GetYearDatabasePath(DateTime.Today.Year));

        Assert.AreEqual(1L, await ObjectCountAsync(connection, "table", "MaintenanceLocationSummary"));
        Assert.AreEqual(1L, await ObjectCountAsync(connection, "table", "MaintenanceTypeSummary"));
        Assert.AreEqual(1L, await ObjectCountAsync(connection, "trigger", "TR_MaintenanceDashboardSummary_Insert"));
        Assert.AreEqual(1L, await ObjectCountAsync(connection, "trigger", "TR_MaintenanceDashboardSummary_Update"));
    }

    [TestMethod]
    public async Task DashboardSummariesTrackCreateEditAndInvalidationExactly()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var occurredAt = DateTimeOffset.Now;

        var first = await service.CreateAsync(Draft(occurredAt, assetId: null) with
        {
            Location = "Plant A",
            MaintenanceType = "HVAC",
            Cost = 10m
        });
        var second = await service.CreateAsync(Draft(occurredAt.AddMinutes(1), assetId: null) with
        {
            Location = "Plant A",
            MaintenanceType = "Electrical",
            Cost = 20m
        });

        var initial = await service.GetDashboardSummaryAsync(first.ReferenceYear);
        var plantA = initial.TopLocations.Single(item => item.Label == "Plant A");
        Assert.AreEqual(2L, plantA.Count);
        Assert.AreEqual(30m, plantA.TotalCost);

        await service.UpdateAsync(first.MaintenanceRecordId, first.ReferenceYear, new MaintenanceRecordEditDraft(
            first.OccurredAt, first.Site, "Plant B", first.Subject, "Electrical",
            first.WorkPerformed, first.PerformedBy, 15m, first.Notes));

        var edited = await service.GetDashboardSummaryAsync(first.ReferenceYear);
        var editedPlantA = edited.TopLocations.Single(item => item.Label == "Plant A");
        var plantB = edited.TopLocations.Single(item => item.Label == "Plant B");
        var electrical = edited.TopMaintenanceTypes.Single(item => item.Label == "Electrical");
        Assert.AreEqual(1L, editedPlantA.Count);
        Assert.AreEqual(20m, editedPlantA.TotalCost);
        Assert.AreEqual(1L, plantB.Count);
        Assert.AreEqual(15m, plantB.TotalCost);
        Assert.AreEqual(2L, electrical.Count);
        Assert.AreEqual(35m, electrical.TotalCost);
        Assert.IsFalse(edited.TopMaintenanceTypes.Any(item => item.Label == "HVAC"));

        await service.MarkInvalidAsync(second.MaintenanceRecordId, second.ReferenceYear, "duplicate");

        var invalidated = await service.GetDashboardSummaryAsync(first.ReferenceYear);
        Assert.IsFalse(invalidated.TopLocations.Any(item => item.Label == "Plant A"));
        var remainingPlantB = invalidated.TopLocations.Single(item => item.Label == "Plant B");
        var remainingElectrical = invalidated.TopMaintenanceTypes.Single(item => item.Label == "Electrical");
        Assert.AreEqual(1L, remainingPlantB.Count);
        Assert.AreEqual(15m, remainingPlantB.TotalCost);
        Assert.AreEqual(1L, remainingElectrical.Count);
        Assert.AreEqual(15m, remainingElectrical.TotalCost);
    }

    [TestMethod]
    public async Task DashboardSummaryVerificationDetectsCorruptionAndRebuildRepairsIt()
    {
        using var scope = new TestDataScope();
        var service = await CreateServiceAsync(scope);
        var record = await service.CreateAsync(Draft(DateTimeOffset.Now, assetId: null) with
        {
            Location = "Verification Plant",
            MaintenanceType = "Verification Type",
            Cost = 42m
        });

        var healthy = await service.VerifyDashboardSummariesAsync(record.ReferenceYear);
        Assert.IsTrue(healthy.IsConsistent);
        Assert.AreEqual(0L, healthy.LocationMismatchCount);
        Assert.AreEqual(0L, healthy.MaintenanceTypeMismatchCount);

        await using (var connection = await OpenAsync(scope.Paths.GetYearDatabasePath(record.ReferenceYear)))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE MaintenanceLocationSummary SET RecordCount = 99 WHERE Label = $label;";
            command.Parameters.AddWithValue("$label", record.Location);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }

        var corrupt = await service.VerifyDashboardSummariesAsync(record.ReferenceYear);
        Assert.IsFalse(corrupt.IsConsistent);
        Assert.AreEqual(1L, corrupt.LocationMismatchCount);
        Assert.AreEqual(0L, corrupt.MaintenanceTypeMismatchCount);

        await service.RebuildDashboardSummariesAsync(record.ReferenceYear);
        var rebuilt = await service.VerifyDashboardSummariesAsync(record.ReferenceYear);
        Assert.IsTrue(rebuilt.IsConsistent);

        var dashboard = await service.GetDashboardSummaryAsync(record.ReferenceYear);
        var location = dashboard.TopLocations.Single(item => item.Label == record.Location);
        Assert.AreEqual(1L, location.Count);
        Assert.AreEqual(42m, location.TotalCost);
    }

    [TestMethod]
    public async Task UnrecognizedDatabaseFileIsNotAdopted()
    {
        using var scope = new TestDataScope();
        Directory.CreateDirectory(scope.Paths.DataDirectory);
        var unrecognized = Path.Combine(scope.Paths.DataDirectory, "Unexpected_Old.db");
        await File.WriteAllBytesAsync(unrecognized, [1, 2, 3]);
        using var initializer = CreateInitializer(scope, out _);
        await initializer.InitializeAsync();
        Assert.IsTrue(File.Exists(unrecognized));
        Assert.IsTrue(File.Exists(scope.Paths.MasterDatabasePath));
    }

    private static DatabaseInitializer CreateInitializer(TestDataScope scope, out DatabaseContextFactory factory)
    {
        factory = new DatabaseContextFactory(scope.Paths);
        return new DatabaseInitializer(factory);
    }

    private static async Task<MaintenanceRecordService> CreateServiceAsync(TestDataScope scope)
    {
        var factory = new DatabaseContextFactory(scope.Paths);
        var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        return new MaintenanceRecordService(factory, annual);
    }

    private static MaintenanceRecordDraft Draft(DateTimeOffset occurredAt, Guid? assetId) => new(
        occurredAt, "Main Site", "Plant Room", "AC repair", "Repair", "Replaced failed relay", "Tech", 25m, "", assetId);

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ObjectCountAsync(SqliteConnection connection, string type, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
