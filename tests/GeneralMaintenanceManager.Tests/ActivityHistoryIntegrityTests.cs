using System.IO;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class ActivityHistoryIntegrityTests : IDisposable
{
    private string _testDirectory = null!;
    private DataPaths _paths = null!;
    private DatabaseInitializer _initializer = null!;
    private ActivityHistoryService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "GeneralMaintenanceManager.ActivityHistoryTests", Guid.NewGuid().ToString("N"));
        _paths = new DataPaths(_testDirectory);
        var factory = new DatabaseContextFactory(_paths);
        _initializer = new DatabaseInitializer(factory);
        _service = new ActivityHistoryService(factory);
        await _initializer.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true);
        if (_paths is not null && Directory.Exists(_paths.BackupsDirectory)) Directory.Delete(_paths.BackupsDirectory, recursive: true);
    }

    [TestMethod]
    public async Task AppendOnlyActivityCanBeReadByRecord()
    {
        var id = Guid.NewGuid();
        await _service.AppendAsync(new ActivityHistoryDraft(
            ActivityTypes.MaintenanceCreated,
            ActivityRecordTypes.Maintenance,
            id,
            "MNT-2026-0000001",
            "Main Hospital",
            "Emergency",
            "AC",
            "Maintenance created.",
            1));

        var timeline = await _service.GetForRecordAsync(ActivityRecordTypes.Maintenance, id);
        Assert.AreEqual(1, timeline.Count);
        Assert.AreEqual(ActivityTypes.MaintenanceCreated, timeline[0].ActivityType);
        Assert.AreEqual("Emergency", timeline[0].Location);
    }

    [TestMethod]
    public async Task MasterSchemaContainsPermanentActivityHistoryTable()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _paths.MasterDatabasePath, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ActivityHistory';";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _initializer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
