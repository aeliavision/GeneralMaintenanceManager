using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class DatabaseArchitectureRedesignTests
{
    [TestMethod]
    public async Task DirectWorkOrderInsertAdvancesAllocatorHighWaterMark()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.UtcNow.Year;
        await using (var context = factory.CreateMasterDbContext())
        {
            context.WorkOrders.Add(CreateSyntheticWorkOrder(year, 25));
            await context.SaveChangesAsync();
        }

        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var service = new WorkOrderService(factory, maintenance);
        var created = await service.CreateAsync(new WorkOrderDraft(
            null, null, "Main Site", "Workshop", "Manual order after direct insert", "General",
            MaintenanceType.GeneralMaintenance, WorkOrderPriority.Normal, "Inspect equipment", "Tester",
            null, string.Empty));

        Assert.AreEqual(26L, created.ReferenceSequence);
        Assert.AreEqual($"WO-{year:0000}-000026", created.WorkOrderNumber);
    }

    [TestMethod]
    public async Task PreventiveGenerationUsesSameSynchronizedWorkOrderSequence()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.UtcNow.Year;
        await using (var context = factory.CreateMasterDbContext())
        {
            context.WorkOrders.Add(CreateSyntheticWorkOrder(year, 41));
            await context.SaveChangesAsync();
        }

        var plans = new MaintenancePlanService(factory);
        var plan = await plans.CreateAsync(new MaintenancePlanDraft(
            null, "Main Site", "Workshop", "Monthly inspection", "Inspection",
            MaintenanceType.PreventiveMaintenance, "Inspect and test", 1, MaintenanceFrequencyUnit.Months,
            DateOnly.FromDateTime(DateTime.Today.AddDays(7)), string.Empty, null, true));

        var generated = await plans.GenerateWorkOrderAsync(plan.MaintenancePlanId);

        Assert.AreEqual(42L, generated.ReferenceSequence);
        Assert.AreEqual($"WO-{year:0000}-000042", generated.WorkOrderNumber);
        Assert.AreEqual(plan.MaintenancePlanId, generated.MaintenancePlanId);
    }

    [TestMethod]
    public async Task ManualAndPreventiveWorkOrderCreationShareAtomicAllocator()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        var maintenance = new MaintenanceRecordService(factory, annual);
        var workOrders = new WorkOrderService(factory, maintenance);
        var plans = new MaintenancePlanService(factory);
        var plan = await plans.CreateAsync(new MaintenancePlanDraft(
            null, "Main Site", "Workshop", "Concurrent preventive", "Inspection",
            MaintenanceType.PreventiveMaintenance, "Inspect", 1, MaintenanceFrequencyUnit.Months,
            DateOnly.FromDateTime(DateTime.Today.AddDays(10)), string.Empty, null, true));

        var manualTask = workOrders.CreateAsync(new WorkOrderDraft(
            null, null, "Main Site", "Workshop", "Concurrent manual", "General",
            MaintenanceType.GeneralMaintenance, WorkOrderPriority.Normal, "Inspect manual request", "Tester",
            null, string.Empty));
        var preventiveTask = plans.GenerateWorkOrderAsync(plan.MaintenancePlanId);

        await Task.WhenAll(manualTask, preventiveTask);
        var created = new[] { await manualTask, await preventiveTask };

        Assert.AreEqual(2, created.Select(item => item.WorkOrderNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.AreEqual(2, created.Select(item => item.ReferenceSequence).Distinct().Count());
    }

    [TestMethod]
    public async Task WorkOrderSequenceHighWaterCannotMoveBackwardOrBeDeleted()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.UtcNow.Year;
        await using (var context = factory.CreateMasterDbContext())
        {
            context.WorkOrders.Add(CreateSyntheticWorkOrder(year, 25));
            await context.SaveChangesAsync();
        }

        await using var verify = factory.CreateMasterDbContext();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => verify.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WorkOrderNumberSequence SET LastValue={24L} WHERE Year={year};"));
        await Assert.ThrowsExactlyAsync<SqliteException>(() => verify.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM WorkOrderNumberSequence WHERE Year={year};"));
    }

    [TestMethod]
    public async Task DatabaseRejectsMismatchedWorkOrderReferenceProjection()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var year = DateTime.UtcNow.Year;
        await using var context = factory.CreateMasterDbContext();
        var row = CreateSyntheticWorkOrder(year, 10);
        row.ReferenceSequence = 11;
        context.WorkOrders.Add(row);

        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [TestMethod]
    public async Task MasterSchemaEnforcesWorkOrderIdentityAndActivityTimelineIndexes()
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
            SELECT type || ':' || name
            FROM sqlite_master
            WHERE name IN (
                'IX_WorkOrders_ReferenceYear_ReferenceSequence',
                'TR_WorkOrders_SyncSequence_AfterInsert',
                'TR_WorkOrders_ImmutableReference',
                'TR_WorkOrderNumberSequence_BlockDecrease',
                'TR_WorkOrderNumberSequence_BlockDelete',
                'IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId',
                'IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId',
                'IX_ActivityHistory_ActivityType_OccurredAtUtcTicks_ActivityHistoryEntryId',
                'IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId',
                'IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId',
                'IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId'
            );
            """;

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        string[] expected =
        [
            "index:IX_WorkOrders_ReferenceYear_ReferenceSequence",
            "trigger:TR_WorkOrders_SyncSequence_AfterInsert",
            "trigger:TR_WorkOrders_ImmutableReference",
            "trigger:TR_WorkOrderNumberSequence_BlockDecrease",
            "trigger:TR_WorkOrderNumberSequence_BlockDelete",
            "index:IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "index:IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "index:IX_ActivityHistory_ActivityType_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "index:IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "index:IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId",
            "index:IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId"
        ];
        foreach (var item in expected) Assert.IsTrue(names.Contains(item), $"Missing database object: {item}");
    }

    [TestMethod]
    public async Task OperationalPagingPlansUseIndexesWithoutTemporarySorts()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using var context = factory.CreateMasterDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT WorkOrderId FROM WorkOrders WHERE IsClosed=0 ORDER BY IsClosed ASC, Priority DESC, SortDueDateOrdinal ASC, ReportedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 201;",
            "IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT WorkOrderId FROM WorkOrders ORDER BY IsClosed ASC, Priority DESC, SortDueDateOrdinal ASC, ReportedAtUtcTicks DESC, ReferenceSequence DESC LIMIT 201;",
            "IX_WorkOrders_IsClosed_Priority_SortDueDateOrdinal_ReportedAtUtcTicks_ReferenceSequence");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_OccurredAtUtcTicks_ActivityHistoryEntryId");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE RecordType='WorkOrder' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_RecordType_OccurredAtUtcTicks_ActivityHistoryEntryId");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE Site='Main Site' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_Site_OccurredAtUtcTicks_ActivityHistoryEntryId");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE Location='Workshop' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_Location_OccurredAtUtcTicks_ActivityHistoryEntryId");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE ActivityType='WorkOrderCreated' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_ActivityType_OccurredAtUtcTicks_ActivityHistoryEntryId");
        await AssertPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT ActivityHistoryEntryId FROM ActivityHistory WHERE ChangedBy='Tester' ORDER BY OccurredAtUtcTicks DESC, ActivityHistoryEntryId DESC LIMIT 101;",
            "IX_ActivityHistory_ChangedBy_OccurredAtUtcTicks_ActivityHistoryEntryId");

        await using var annual = factory.CreateAnnualDbContext(DateTime.Today.Year);
        var annualConnection = annual.Database.GetDbConnection();
        await annualConnection.OpenAsync();
        await AssertPlanAsync(annualConnection,
            "EXPLAIN QUERY PLAN SELECT MaintenanceActivityId FROM MaintenanceActivity WHERE ActivityType='Created' ORDER BY OccurredAtUtcTicks DESC, MaintenanceActivityId DESC LIMIT 101;",
            "IX_MaintenanceActivity_ActivityType_OccurredAtUtcTicks_MaintenanceActivityId");
    }

    private static async Task AssertPlanAsync(System.Data.Common.DbConnection connection, string sql, string requiredIndex)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.IsTrue(details.Any(detail => detail.Contains(requiredIndex, StringComparison.Ordinal)),
            $"Expected {requiredIndex}. Plan: {string.Join(" | ", details)}");
        Assert.IsFalse(details.Any(detail => detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
            $"Unexpected temporary sort. Plan: {string.Join(" | ", details)}");
    }

    private static WorkOrder CreateSyntheticWorkOrder(int year, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkOrder
        {
            WorkOrderId = Guid.NewGuid(),
            WorkOrderNumber = $"WO-{year:0000}-{sequence:000000}",
            ReferenceYear = year,
            ReferenceSequence = sequence,
            Site = "Main Site",
            Location = "Workshop",
            Title = $"Synthetic work order {sequence}",
            MaintenanceCategory = "General",
            MaintenanceType = MaintenanceType.GeneralMaintenance,
            Priority = WorkOrderPriority.Normal,
            ProblemDescription = "Synthetic sequence test",
            RequestedBy = "Test",
            ReportedAtUtc = now,
            ReportedAtUtcTicks = now.UtcTicks,
            Status = WorkOrderStatus.New,
            CreatedBy = "Test",
            CreatedAtUtc = now,
            Revision = 1,
            ActivityAtUtcTicks = now.UtcTicks,
            IsClosed = false,
            SortDueDateOrdinal = int.MaxValue
        };
    }
}
