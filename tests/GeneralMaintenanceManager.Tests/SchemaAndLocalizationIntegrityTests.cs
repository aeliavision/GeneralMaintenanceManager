using System.Globalization;
using System.Xml.Linq;
using GeneralMaintenanceManager.App.Models;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class SchemaAndLocalizationIntegrityTests
{
    [TestMethod]
    public async Task FreshMasterAndAnnualSchemasExposeRequiredIdentityIndexesAndTriggers()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using var master = await OpenAsync(scope.Paths.MasterDatabasePath);
        Assert.AreEqual(DatabaseInitializer.MasterFormatId, await ScalarStringAsync(master, "SELECT FormatId FROM SchemaInfo WHERE Id=1;"));
        Assert.AreEqual(DatabaseInitializer.MasterSchemaVersion, await ScalarIntAsync(master, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;"));
        await AssertObjectAsync(master, "table", "WorkOrderCompletionIntents");
        await AssertObjectAsync(master, "index", "IX_WorkOrderCompletionIntents_WorkOrderId");
        await AssertObjectAsync(master, "index", "IX_WorkOrders_MaintenancePlanId");
        await AssertObjectAsync(master, "trigger", "TR_ActivityHistory_BlockUpdate");
        Assert.AreEqual(0, await ObjectCountAsync(master, "table", "MaintenanceRecords"));
        Assert.AreEqual(0, await ObjectCountAsync(master, "table", "MaintenanceEvents"));

        var annualPath = scope.Paths.GetYearDatabasePath(DateTime.Today.Year);
        await using var annual = await OpenAsync(annualPath);
        Assert.AreEqual(DatabaseInitializer.AnnualFormatId, await ScalarStringAsync(annual, "SELECT FormatId FROM SchemaInfo WHERE Id=1;"));
        Assert.AreEqual(DatabaseInitializer.AnnualSchemaVersion, await ScalarIntAsync(annual, "SELECT SchemaVersion FROM SchemaInfo WHERE Id=1;"));
        await AssertObjectAsync(annual, "table", "MaintenanceRecords");
        await AssertObjectAsync(annual, "table", "MaintenanceActivity");
        await AssertObjectAsync(annual, "index", "IX_MaintenanceRecords_GeneratedByWorkOrderId");
        await AssertObjectAsync(annual, "index", "IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence");
        await AssertObjectAsync(annual, "index", "IX_MaintenanceActivity_OccurredAtUtcTicks_MaintenanceActivityId");
        await AssertObjectAsync(annual, "trigger", "TR_MaintenanceRecords_ReferenceFormat_Update");
        Assert.AreEqual(0, await ObjectCountAsync(annual, "table", "MaintenanceEvents"));
        Assert.AreEqual(0, await ObjectCountAsync(annual, "table", "MachineEvents"));
    }

    [TestMethod]
    public async Task ExistingSupportedAnnualSchemaWithMissingRequiredCompositeIndexIsRejectedWithoutStartupRepair()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        var year = DateTime.Today.Year;
        var annualPath = scope.Paths.GetYearDatabasePath(year);

        using (var initializer = new DatabaseInitializer(factory))
        {
            await initializer.InitializeAsync();
        }

        await using (var annual = await OpenAsync(annualPath))
        {
            await ExecuteAsync(annual, "DROP INDEX IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence;");
            Assert.AreEqual(
                0,
                await ObjectCountAsync(
                    annual,
                    "index",
                    "IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence"));
        }

        using var verifier = new DatabaseInitializer(factory);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => verifier.EnsureYearAsync(year));
        StringAssert.Contains(
            ex.Message,
            "missing required index 'IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence'");

        await using var rejectedAnnual = await OpenAsync(annualPath);
        Assert.AreEqual(
            0,
            await ObjectCountAsync(
                rejectedAnnual,
                "index",
                "IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence"));
    }

    [TestMethod]
    public async Task UnsupportedAnnualSchemaIdentityIsRejectedWithoutRepairingKnownIndex()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        var year = DateTime.Today.Year;
        var annualPath = scope.Paths.GetYearDatabasePath(year);

        using (var initializer = new DatabaseInitializer(factory))
        {
            await initializer.InitializeAsync();
        }

        await using (var annual = await OpenAsync(annualPath))
        {
            await ExecuteAsync(annual, "DROP INDEX IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence;");
            await ExecuteAsync(annual, "DROP TRIGGER IF EXISTS TR_SchemaInfo_BlockUpdate;");
            await ExecuteAsync(annual, "UPDATE SchemaInfo SET SchemaVersion=999 WHERE Id=1;");
        }

        using var verifier = new DatabaseInitializer(factory);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => verifier.EnsureYearAsync(year));
        StringAssert.Contains(ex.Message, "No migration was attempted");

        await using var rejectedAnnual = await OpenAsync(annualPath);
        Assert.AreEqual(0, await ObjectCountAsync(rejectedAnnual, "index", "IX_MaintenanceRecords_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence"));
    }

    [TestMethod]
    public async Task UnsupportedMasterSchemaIdentityIsRejectedWithoutMigration()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using (var initializer = new DatabaseInitializer(factory))
        {
            await initializer.InitializeAsync();
        }

        await using (var connection = await OpenAsync(scope.Paths.MasterDatabasePath))
        {
            await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS TR_SchemaInfo_BlockUpdate;");
            await ExecuteAsync(connection, "UPDATE SchemaInfo SET SchemaVersion=999 WHERE Id=1;");
        }

        using var verifier = new DatabaseInitializer(factory);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => verifier.EnsureMasterAsync());
        StringAssert.Contains(ex.Message, "No migration was attempted");
    }

    [TestMethod]
    public void EnglishAndArabicResourcesHaveParityAndCoverDomainPresentationKeys()
    {
        var english = LoadResourceKeys("Strings.en.xaml");
        var arabic = LoadResourceKeys("Strings.ar.xaml");
        CollectionAssert.AreEquivalent(english.ToArray(), arabic.ToArray());

        string[] required =
        [
            "MaintenanceTypeGeneral", "MaintenanceTypeCorrective", "MaintenanceTypePreventive",
            "MaintenanceTypeInspection", "MaintenanceTypeRoutineService", "MaintenanceTypeBreakdown",
            "MaintenanceTypeInstallation", "MaintenanceTypeRelocation", "MaintenanceTypeReplacement",
            "MaintenanceTypeCalibration", "WorkOrderStatusNew", "WorkOrderStatusAssigned",
            "WorkOrderStatusInProgress", "WorkOrderStatusOnHold", "WorkOrderStatusCompleted",
            "WorkOrderStatusCancelled", "PriorityLow", "PriorityNormal", "PriorityHigh", "PriorityUrgent",
            "FrequencyDays", "FrequencyWeeks", "FrequencyMonths", "FrequencyYears",
            "ActivityMaintenanceCreated", "ActivityMaintenanceEdited", "MaintenanceMarkedInvalid",
            "ActivityWorkOrderCreated", "ActivityWorkOrderAssigned", "ActivityWorkOrderStarted",
            "ActivityWorkOrderResumed", "ActivityWorkOrderPutOnHold", "ActivityWorkOrderCompleted",
            "ActivityWorkOrderCompletionRecovered", "ActivityWorkOrderCancelled", "ActivityWorkOrderEdited",
            "ActivityPreventivePlanCreated", "ActivityPreventivePlanEdited", "ActivityPreventivePlanActivated",
            "ActivityPreventivePlanPaused", "ActivityPreventivePlanDue", "ActivityPreventivePlanGeneratedWorkOrder",
            "ActivityPreventivePlanCompletedOccurrence"
        ];

        foreach (var key in required)
        {
            Assert.IsTrue(english.Contains(key), $"Missing English localization key '{key}'.");
            Assert.IsTrue(arabic.Contains(key), $"Missing Arabic localization key '{key}'.");
        }

        var localization = new KeyEchoLocalizationService();
        foreach (WorkOrderStatus value in Enum.GetValues<WorkOrderStatus>())
            Assert.IsTrue(MaintenanceTextPresentation.LocalizeWorkOrderStatus(value, localization).Length > 0);
        foreach (WorkOrderPriority value in Enum.GetValues<WorkOrderPriority>())
            Assert.IsTrue(MaintenanceTextPresentation.LocalizeWorkOrderPriority(value, localization).StartsWith("Priority", StringComparison.Ordinal));
        foreach (MaintenanceFrequencyUnit value in Enum.GetValues<MaintenanceFrequencyUnit>())
            Assert.IsTrue(MaintenanceTextPresentation.LocalizeFrequencyUnit(value, localization).StartsWith("Frequency", StringComparison.Ordinal));
        foreach (MaintenanceType value in MaintenanceTypePresentation.CreateMaintenanceTypeChoices(localization).Select(item => item.Value))
            Assert.IsFalse(string.IsNullOrWhiteSpace(MaintenanceTypePresentation.GetDisplayText(value, localization)));
    }


    [TestMethod]
    public void MaintenanceActivityDescriptionsAreHumanReadableInsteadOfRawAuditPairs()
    {
        var localization = new KeyEchoLocalizationService();
        var created = new MaintenanceActivity
        {
            ActivityType = MaintenanceActivityTypes.Created,
            Changes = "Type=Cleaning; Location=kk"
        };

        var createdText = MaintenanceTextPresentation.FormatMaintenanceActivityDescription(created, localization);
        Assert.AreEqual("MaintenanceType: MaintenanceTypeCleaning  •  Location: kk", createdText);
        Assert.IsFalse(createdText.Contains("Type=", StringComparison.Ordinal));

        var invalid = new MaintenanceActivity
        {
            ActivityType = MaintenanceActivityTypes.MarkedInvalid,
            Reason = "Duplicate record",
            Changes = "IsInvalid=true"
        };
        Assert.AreEqual("Duplicate record", MaintenanceTextPresentation.FormatMaintenanceActivityDescription(invalid, localization));
    }

    private static HashSet<string> LoadResourceKeys(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);
        Assert.IsTrue(File.Exists(path), $"Localization test resource was not copied: {path}");
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!.Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task AssertObjectAsync(SqliteConnection connection, string type, string name) =>
        Assert.AreEqual(1, await ObjectCountAsync(connection, type, name), $"Missing {type} '{name}'.");

    private static async Task<int> ObjectCountAsync(SqliteConnection connection, string type, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class KeyEchoLocalizationService : ILocalizationService
    {
        public string CurrentLanguageCode => "en";
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public event EventHandler? LanguageChanged { add { } remove { } }
        public string GetString(string key) => key;
        public string Format(string key, params object?[] arguments) => key;
        public string LocalizeCondition(string? condition) => condition ?? string.Empty;
        public void SetLanguage(string languageCode) { }
    }
}
