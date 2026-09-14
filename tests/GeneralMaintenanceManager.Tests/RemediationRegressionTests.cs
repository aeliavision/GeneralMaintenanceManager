using System.IO;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class RemediationRegressionTests
{
    [TestMethod]
    public async Task DatabaseContextsParticipateInSharedActivityGate()
    {
        using var scope = new TestDataScope();
        using var gate = new DatabaseActivityGate();
        var factory = new DatabaseContextFactory(scope.Paths, gate);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var context = factory.CreateMasterDbContext();
        var exclusiveTask = gate.EnterExclusiveAsync();
        await Task.Delay(50);
        Assert.IsFalse(exclusiveTask.IsCompleted, "An open DbContext must keep destructive database operations out.");

        await context.DisposeAsync();
        using var exclusive = await exclusiveTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(exclusiveTask.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task ExclusiveDatabaseActivityLeaseAllowsContextsInSameAsyncFlow()
    {
        using var scope = new TestDataScope();
        using var gate = new DatabaseActivityGate();
        var factory = new DatabaseContextFactory(scope.Paths, gate);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        using var exclusive = await gate.EnterExclusiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await using var context = factory.CreateMasterDbContext();
        Assert.IsTrue(await context.Database.CanConnectAsync().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task FullHistoryPrintSelectionStopsAtConfiguredSafetyLimit()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var history = new MaintenanceHistoryService(maintenance);
        var assets = new AssetService(factory);
        var asset = await assets.AddAsync(new AssetDraft(
            "PRINT-001", "Main Site", "Workshop", "Print test asset", "Test", "Maker", "",
            "Model", "SN-PRINT-001", "Good", "In Service", "", null, null, null, null, ""));

        for (var index = 0; index < 3; index++)
        {
            await maintenance.CreateAsync(new MaintenanceRecordDraft(
                DateTimeOffset.Now.AddMinutes(-index), "Main Site", "Workshop", $"Print {index}",
                "General Maintenance", "Checked", "Technician", null, "", asset.AssetId));
        }

        var selection = await history.GetHistoryForPrintAsync(asset.AssetId, maximumRecords: 2);

        Assert.AreEqual(2, selection.Items.Count);
        Assert.IsTrue(selection.IsTruncated);
    }

    [TestMethod]
    public async Task ActivityHistorySearchInfrastructureIsExternalContentFtsAndFindsNewRows()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var service = new ActivityHistoryService(factory);

        var created = await service.AppendAsync(new ActivityHistoryDraft(
            ActivityTypes.WorkOrderEdited, ActivityRecordTypes.WorkOrder, Guid.NewGuid(),
            "WO-FTS-000001", "North Plant", "Pump Room", "Hydraulic Pump Alpha",
            "Pressure regulator replaced successfully.", 1, Changes: "Pressure=restored"));

        var page = await service.GetPageAsync(new ActivityHistoryQuery(SearchText: "regulator", PageSize: 20));
        Assert.IsTrue(page.Items.Any(item => item.ActivityHistoryEntryId == created.ActivityHistoryEntryId));

        await using var context = factory.CreateMasterDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ActivityHistorySearch';";
        var sql = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        StringAssert.Contains(sql, "content='ActivityHistory'");
        StringAssert.Contains(sql, "content_rowid='rowid'");
    }

    [TestMethod]
    public async Task ActivityHistorySearchFallsBackWhenExistingFtsNeedsExplicitRebuild()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var service = new ActivityHistoryService(factory);

        var created = await service.AppendAsync(new ActivityHistoryDraft(
            ActivityTypes.WorkOrderEdited, ActivityRecordTypes.WorkOrder, Guid.NewGuid(),
            "WO-FTS-FALLBACK", "South Plant", "Utility Room", "Fallback test",
            "Cooling regulator replaced.", 1, Changes: "Cooling=stable"));

        await using (var context = factory.CreateMasterDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE SearchIndexState SET IsReady=0 WHERE SearchName='ActivityHistorySearch';");
        }

        var page = await service.GetPageAsync(new ActivityHistoryQuery(SearchText: "regulator", PageSize: 20));
        Assert.IsTrue(page.Items.Any(item => item.ActivityHistoryEntryId == created.ActivityHistoryEntryId));
    }

    [TestMethod]
    public async Task CsvExportNeutralizesSpreadsheetFormulaPrefixes()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var assets = new AssetService(factory);
        var dangerousNames = new[]
        {
            "=1+1",
            "+SUM(A1:A2)",
            "-10+20",
            "@SUM(A1:A2)"
        };

        for (var index = 0; index < dangerousNames.Length; index++)
        {
            await assets.AddAsync(new AssetDraft(
                $"CSV-{index + 1:000}",
                "Main Site",
                "Workshop",
                dangerousNames[index],
                "Test",
                "Maker",
                "",
                "Model",
                $"SN-{index + 1:000}",
                "Good",
                "In Service",
                "",
                null,
                null,
                null,
                null,
                ""));
        }

        var export = new AssetExportService(factory);
        var path = Path.Combine(scope.Paths.BackupsDirectory, "formula-hardening.csv");
        var count = await export.ExportCsvAsync(path);
        var csv = await File.ReadAllTextAsync(path);

        Assert.AreEqual(dangerousNames.Length, count);
        Assert.IsTrue(csv.Contains("'=1+1", StringComparison.Ordinal));
        Assert.IsTrue(csv.Contains("'+SUM(A1:A2)", StringComparison.Ordinal));
        Assert.IsTrue(csv.Contains("'-10+20", StringComparison.Ordinal));
        Assert.IsTrue(csv.Contains("'@SUM(A1:A2)", StringComparison.Ordinal));
        Assert.IsFalse(csv.Contains(",=1+1,", StringComparison.Ordinal));
        Assert.IsFalse(csv.Contains(",+SUM(A1:A2),", StringComparison.Ordinal));
        Assert.IsFalse(csv.Contains(",-10+20,", StringComparison.Ordinal));
        Assert.IsFalse(csv.Contains(",@SUM(A1:A2),", StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task ActivityHistoryPagingIsBoundedAndLosslessAcrossTimestampTies()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var service = new ActivityHistoryService(factory);
        var occurredAt = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 7; index++)
        {
            await service.AppendAsync(new ActivityHistoryDraft(
                ActivityTypes.WorkOrderEdited,
                ActivityRecordTypes.WorkOrder,
                Guid.NewGuid(),
                $"WO-TIE-{index:00}",
                "Main Site",
                "Workshop",
                "Paging tie",
                "Synthetic same-timestamp paging event.",
                1,
                OccurredAtUtc: occurredAt));
        }

        var first = await service.GetPageAsync(new ActivityHistoryQuery(PageSize: 3));
        var second = await service.GetPageAsync(new ActivityHistoryQuery(PageSize: 3, Cursor: first.NextCursor));
        var firstIds = first.Items.Select(item => item.ActivityHistoryEntryId).ToHashSet();

        Assert.AreEqual(3, first.Items.Count);
        Assert.AreEqual(3, second.Items.Count);
        Assert.IsTrue(first.HasMore);
        Assert.IsNotNull(first.NextCursor);
        Assert.IsFalse(second.Items.Any(item => firstIds.Contains(item.ActivityHistoryEntryId)));
    }

    [TestMethod]
    public async Task PreventivePlanReadsArePureAndConcurrentDueProcessingConverges()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var firstService = new MaintenancePlanService(factory);
        var secondService = new MaintenancePlanService(factory);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var plan = await firstService.CreateAsync(new MaintenancePlanDraft(
            null,
            "Main Site",
            "Workshop",
            "Monthly inspection",
            "Preventive",
            MaintenanceType.PreventiveMaintenance,
            "Inspect and document condition.",
            1,
            MaintenanceFrequencyUnit.Months,
            today,
            "",
            null,
            true));

        _ = await firstService.GetAllAsync();
        await using (var before = factory.CreateMasterDbContext())
        {
            var readOnlyDueEvents = await before.ActivityHistory.CountAsync(item =>
                item.RecordId == plan.MaintenancePlanId && item.ActivityType == ActivityTypes.PreventivePlanDue);
            Assert.AreEqual(0, readOnlyDueEvents);
        }

        await Task.WhenAll(
            firstService.ProcessDueActivitiesAsync(today),
            secondService.ProcessDueActivitiesAsync(today));

        await using var after = factory.CreateMasterDbContext();
        var dueEvents = await after.ActivityHistory.CountAsync(item =>
            item.RecordId == plan.MaintenancePlanId && item.ActivityType == ActivityTypes.PreventivePlanDue);
        Assert.AreEqual(1, dueEvents);
    }

    [TestMethod]
    public async Task WorkOrderFtsSearchFindsCreatedOrder()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var workOrders = new WorkOrderService(factory, maintenance);

        var created = await workOrders.CreateAsync(new WorkOrderDraft(
            null,
            null,
            "North Plant",
            "Pump Room",
            "Hydraulic Pump Alpha",
            "Corrective",
            MaintenanceType.CorrectiveMaintenance,
            WorkOrderPriority.High,
            "Pump loses pressure under load.",
            "QA",
            null,
            "Technician A",
            "Search regression"));

        var page = await workOrders.GetPageAsync(new WorkOrderQuery(SearchText: "Hydraulic Pump", PageSize: 10));
        Assert.IsTrue(page.Items.Any(item => item.WorkOrderId == created.WorkOrderId));
    }

    [TestMethod]
    public async Task AllActivityIncludesAnnualMaintenanceAuditEvents()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var activity = new ActivityHistoryService(factory);
        var global = new GlobalHistoryQueryService(maintenance, activity);
        var occurredAt = DateTimeOffset.Now;

        var record = await maintenance.CreateAsync(new MaintenanceRecordDraft(
            occurredAt,
            "Main Site",
            "Workshop",
            "Audit subject",
            "General Maintenance",
            "Initial work",
            "Technician",
            null,
            ""));
        await maintenance.UpdateAsync(record.MaintenanceRecordId, record.ReferenceYear, new MaintenanceRecordEditDraft(
            occurredAt,
            "Main Site",
            "Workshop",
            "Audit subject corrected",
            "General Maintenance",
            "Corrected work",
            "Technician",
            null,
            "Correction"));
        await maintenance.MarkInvalidAsync(record.MaintenanceRecordId, record.ReferenceYear, "Test invalidation");

        var page = await global.GetPageAsync(new GlobalHistoryQuery(
            Mode: GlobalHistoryModes.AllActivity,
            Year: record.ReferenceYear,
            PageSize: 20));
        var types = page.Items
            .Where(item => item.Activity?.RecordId == record.MaintenanceRecordId)
            .Select(item => item.Activity!.ActivityType)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(types.Contains(ActivityTypes.MaintenanceCreated));
        Assert.IsTrue(types.Contains(ActivityTypes.MaintenanceEdited));
        Assert.IsTrue(types.Contains(ActivityTypes.MaintenanceMarkedInvalid));
    }

    [TestMethod]
    public async Task AdditiveInfrastructureContainsWorkOrderFtsAndPreventiveOccurrenceGuard()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using var context = factory.CreateMasterDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type='table' AND name IN ('WorkOrderSearch','PreventiveDueOccurrence'))
               OR (type='trigger' AND name IN ('TR_WorkOrderSearch_Insert','TR_WorkOrderSearch_Update','TR_WorkOrderSearch_Delete'));
            """;
        Assert.AreEqual(5L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task IncludeInvalidFilteredPagingReturnsBothValidityBranchesWithoutDuplicates()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var assets = new AssetService(factory);
        var asset = await assets.AddAsync(new AssetDraft(
            "INVALID-001", "Main Site", "Plant Room", "Invalid paging asset", "Test", "Maker", "",
            "Model", "SN-INVALID-001", "Good", "In Service", "", null, null, null, null, ""));

        var first = await maintenance.CreateAsync(new MaintenanceRecordDraft(
            DateTimeOffset.Now.AddMinutes(-2), "Main Site", "Plant Room", "Older",
            "HVAC", "Checked", "Technician", null, "", asset.AssetId));
        var second = await maintenance.CreateAsync(new MaintenanceRecordDraft(
            DateTimeOffset.Now.AddMinutes(-1), "Main Site", "Plant Room", "Newer",
            "HVAC", "Repaired", "Technician", null, "", asset.AssetId));
        await maintenance.MarkInvalidAsync(second.MaintenanceRecordId, second.ReferenceYear, "duplicate");

        var page = await maintenance.GetPageAsync(new MaintenanceQuery(
            Year: first.ReferenceYear,
            AssetId: asset.AssetId,
            MaintenanceType: "HVAC",
            IncludeInvalid: true,
            PageSize: 20));

        CollectionAssert.AreEquivalent(
            new[] { first.MaintenanceRecordId, second.MaintenanceRecordId },
            page.Items.Select(item => item.MaintenanceRecordId).ToArray());
        Assert.AreEqual(2, page.Items.Select(item => item.MaintenanceRecordId).Distinct().Count());
    }

    [TestMethod]
    public async Task AssetFilteredAggregateUsesCanonicalSqliteGuidBinding()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var assets = new AssetService(factory);
        var asset = await assets.AddAsync(new AssetDraft(
            "GUID-AGG-001", "Main Site", "Plant Room", "Guid aggregate asset", "Test", "Maker", "",
            "Model", "SN-GUID-AGG-001", "Good", "In Service", "", null, null, null, null, ""));

        var record = await maintenance.CreateAsync(new MaintenanceRecordDraft(
            DateTimeOffset.Now, "Main Site", "Plant Room", "Guid aggregate",
            "HVAC", "Checked", "Technician", 12.34m, "", asset.AssetId));

        var aggregate = await maintenance.GetAggregateAsync(new MaintenanceQuery(
            Year: record.ReferenceYear, AssetId: asset.AssetId, IncludeInvalid: false));

        Assert.AreEqual(1L, aggregate.Count);
        Assert.AreEqual(12.34m, aggregate.TotalCost);
    }

    [TestMethod]
    public async Task AllActivityMaintenanceTimelinePlanUsesTemporalIndexWithoutTempSort()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.Today.Year;
        await using var context = factory.CreateAnnualDbContext(year);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT activity.MaintenanceActivityId, activity.OccurredAtUtcTicks, record.MaintenanceRecordId
            FROM MaintenanceActivity AS activity
            INNER JOIN MaintenanceRecords AS record
                ON record.MaintenanceRecordId = activity.MaintenanceRecordId
            ORDER BY activity.OccurredAtUtcTicks DESC, activity.MaintenanceActivityId DESC
            LIMIT 101;
            """;

        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));

        Assert.IsTrue(
            details.Any(detail => detail.Contains(
                "IX_MaintenanceActivity_OccurredAtUtcTicks_MaintenanceActivityId",
                StringComparison.Ordinal)),
            "All Activity must use the annual temporal MaintenanceActivity index. Plan: " + string.Join(" | ", details));
        Assert.IsFalse(
            details.Any(detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
            "All Activity must not sort the annual MaintenanceActivity table into a temporary B-tree. Plan: " + string.Join(" | ", details));
    }

    [TestMethod]
    public async Task MaintenanceTypePagingPlanUsesCompositeIndexWithoutTempSort()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.Today.Year;
        await using var context = factory.CreateAnnualDbContext(year);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        foreach (var isInvalid in new[] { 0, 1 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT MaintenanceRecordId, MaintenanceType, IsInvalid, OccurredAtUtcTicks, CreatedAtUtcTicks, ReferenceSequence
                FROM MaintenanceRecords
                WHERE IsInvalid = $isInvalid AND MaintenanceType = $type
                ORDER BY OccurredAtUtcTicks DESC, CreatedAtUtcTicks DESC, ReferenceSequence DESC
                LIMIT 201;
                """;
            var invalidParameter = command.CreateParameter();
            invalidParameter.ParameterName = "$isInvalid";
            invalidParameter.Value = isInvalid;
            command.Parameters.Add(invalidParameter);
            var typeParameter = command.CreateParameter();
            typeParameter.ParameterName = "$type";
            typeParameter.Value = "HVAC";
            command.Parameters.Add(typeParameter);

            var details = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                details.Add(reader.GetString(3));

            Assert.IsTrue(
                details.Any(detail => detail.Contains(
                    "IX_MaintenanceRecords_MaintenanceType_IsInvalid_OccurredAtUtcTicks_CreatedAtUtcTicks_ReferenceSequence",
                    StringComparison.Ordinal)),
                string.Join(Environment.NewLine, details));
            Assert.IsFalse(
                details.Any(detail => detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
                string.Join(Environment.NewLine, details));
        }
    }

    [TestMethod]
    public void DiagnosticLoggerWritesBoundedSystemLogFile()
    {
        using var scope = new TestDataScope();
        var logger = new DiagnosticLogger(scope.Paths);
        logger.Info("Regression", "diagnostic logger smoke test");

        var files = Directory.GetFiles(scope.Paths.SystemLogsDirectory, "gmm-*.log");
        Assert.AreEqual(1, files.Length);
        var text = File.ReadAllText(files[0]);
        Assert.IsTrue(text.Contains("[Regression]", StringComparison.Ordinal));
    }

}
