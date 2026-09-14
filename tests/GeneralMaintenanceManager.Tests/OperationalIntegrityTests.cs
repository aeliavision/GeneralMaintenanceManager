using System.IO.Compression;
using GeneralMaintenanceManager.App.Services;
using GeneralMaintenanceManager.Core.Entities;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using GeneralMaintenanceManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GeneralMaintenanceManager.Tests;

[TestClass]
public sealed class OperationalIntegrityTests
{
    private static readonly string[] ExpectedCustomMaintenanceTypes = ["Electrical", "HVAC"];

    [TestMethod]
    public async Task RepeatedWorkOrderCompletionConvergesToExactlyOnePermanentMaintenanceRecord()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out var maintenance, out var workOrders);

        var order = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech"));
        var completion = CompletionFor(order.WorkOrderId);

        await workOrders.CompleteAsync(completion);
        var completedOnce = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(completedOnce);
        Assert.AreEqual(WorkOrderStatus.Completed, completedOnce.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(completedOnce.GeneratedMaintenanceNumber));
        var permanentMnt = completedOnce.GeneratedMaintenanceNumber;

        await workOrders.CompleteAsync(completion);
        await workOrders.RecoverPendingCompletionsAsync();
        var completedAgain = await workOrders.GetByIdAsync(order.WorkOrderId);

        Assert.IsNotNull(completedAgain);
        Assert.AreEqual(permanentMnt, completedAgain.GeneratedMaintenanceNumber);
        var canonical = await maintenance.GetByNumberAsync(permanentMnt);
        Assert.IsNotNull(canonical);
        Assert.AreEqual(order.WorkOrderId, canonical.GeneratedByWorkOrderId);

        await using var annual = factory.CreateAnnualDbContext(canonical.ReferenceYear);
        Assert.AreEqual(1, await annual.MaintenanceRecords.CountAsync(item => item.GeneratedByWorkOrderId == order.WorkOrderId));

        await using var master = factory.CreateMasterDbContext();
        Assert.AreEqual(1, await master.WorkOrderCompletionIntents.CountAsync(item => item.WorkOrderId == order.WorkOrderId));
        var intent = await master.WorkOrderCompletionIntents.SingleAsync(item => item.WorkOrderId == order.WorkOrderId);
        Assert.AreEqual(WorkOrderCompletionIntentStates.Finalized, intent.State);
    }


    [TestMethod]
    public async Task ReservedCompletionIntentIsRecoveredWithoutAllocatingSecondMaintenanceReference()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out var maintenance, out var workOrders);
        var order = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech"));
        var completion = CompletionFor(order.WorkOrderId);

        var prepare = typeof(WorkOrderService).GetMethod(
            "PrepareCompletionIntentAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new AssertFailedException("The recovery integration test requires the durable intent preparation step.");
        var prepareTask = (Task<WorkOrderCompletionIntent>)prepare.Invoke(
            workOrders,
            [completion, CancellationToken.None])!;
        var reserved = await prepareTask;
        Assert.AreEqual(WorkOrderCompletionIntentStates.Reserved, reserved.State);
        Assert.IsNull(await maintenance.GetByNumberAsync(reserved.MaintenanceNumber));

        await workOrders.RecoverPendingCompletionsAsync();
        await workOrders.RecoverPendingCompletionsAsync();

        var recoveredOrder = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(recoveredOrder);
        Assert.AreEqual(WorkOrderStatus.Completed, recoveredOrder.Status);
        Assert.AreEqual(reserved.MaintenanceNumber, recoveredOrder.GeneratedMaintenanceNumber);

        var canonical = await maintenance.GetByNumberAsync(reserved.MaintenanceNumber);
        Assert.IsNotNull(canonical);
        Assert.AreEqual(reserved.MaintenanceRecordId, canonical.MaintenanceRecordId);
        Assert.AreEqual(order.WorkOrderId, canonical.GeneratedByWorkOrderId);

        await using var annual = factory.CreateAnnualDbContext(reserved.ReferenceYear);
        Assert.AreEqual(1, await annual.MaintenanceRecords.CountAsync(item => item.GeneratedByWorkOrderId == order.WorkOrderId));
        await using var master = factory.CreateMasterDbContext();
        var intent = await master.WorkOrderCompletionIntents.SingleAsync(item => item.WorkOrderId == order.WorkOrderId);
        Assert.AreEqual(WorkOrderCompletionIntentStates.Finalized, intent.State);
        Assert.IsNotNull(intent.AnnualWrittenAtUtc);
        Assert.IsNotNull(intent.FinalizedAtUtc);
    }

    [TestMethod]
    public async Task AnnualWriteBeforeMasterFinalizationIsRecoveredUsingReservedIdentity()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out var maintenance, out var workOrders);
        var order = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech"));
        var completion = CompletionFor(order.WorkOrderId);

        var prepare = typeof(WorkOrderService).GetMethod(
            "PrepareCompletionIntentAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new AssertFailedException("The recovery integration test requires the durable intent preparation step.");
        var prepareTask = (Task<WorkOrderCompletionIntent>)prepare.Invoke(
            workOrders,
            [completion, CancellationToken.None])!;
        var reserved = await prepareTask;

        var written = await maintenance.EnsureWorkOrderMaintenanceAsync(new WorkOrderGeneratedMaintenanceDraft(
            reserved.WorkOrderId,
            reserved.MaintenanceRecordId,
            reserved.MaintenanceNumber,
            reserved.ReferenceYear,
            reserved.ReferenceSequence,
            reserved.OccurredAt,
            reserved.Site,
            reserved.Location,
            reserved.Subject,
            reserved.MaintenanceType,
            reserved.WorkPerformed,
            reserved.PerformedBy,
            reserved.Cost,
            reserved.MaintenanceNotes,
            reserved.AssetId,
            reserved.AssetNumberSnapshot,
            reserved.AssetNameSnapshot,
            reserved.WorkOrderNumberSnapshot));

        Assert.AreEqual(reserved.MaintenanceRecordId, written.MaintenanceRecordId);
        await using (var beforeRecovery = factory.CreateMasterDbContext())
        {
            var pending = await beforeRecovery.WorkOrderCompletionIntents.AsNoTracking()
                .SingleAsync(item => item.WorkOrderId == order.WorkOrderId);
            Assert.AreEqual(WorkOrderCompletionIntentStates.Reserved, pending.State);
        }

        await workOrders.RecoverPendingCompletionsAsync();
        await workOrders.RecoverPendingCompletionsAsync();

        var recoveredOrder = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(recoveredOrder);
        Assert.AreEqual(WorkOrderStatus.Completed, recoveredOrder.Status);
        Assert.AreEqual(reserved.MaintenanceNumber, recoveredOrder.GeneratedMaintenanceNumber);
        await using var annual = factory.CreateAnnualDbContext(reserved.ReferenceYear);
        Assert.AreEqual(1, await annual.MaintenanceRecords.CountAsync(item => item.GeneratedByWorkOrderId == order.WorkOrderId));
        await using var master = factory.CreateMasterDbContext();
        var finalized = await master.WorkOrderCompletionIntents.AsNoTracking().SingleAsync(item => item.WorkOrderId == order.WorkOrderId);
        Assert.AreEqual(WorkOrderCompletionIntentStates.Finalized, finalized.State);
        Assert.IsNotNull(finalized.AnnualWrittenAtUtc);
        Assert.IsNotNull(finalized.FinalizedAtUtc);
    }

    [TestMethod]
    public async Task GenericWorkOrderEditCannotBypassAssigneeLifecycleRules()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out _, out var workOrders);

        var originalDraft = WorkOrderDraftFor(assignedTo: "Tech A");
        var order = await workOrders.CreateAsync(originalDraft);
        await workOrders.StartAsync(order.WorkOrderId);

        var illegalEdit = originalDraft with { AssignedTo = "Tech B", Notes = "ordinary edit" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workOrders.UpdateAsync(order.WorkOrderId, illegalEdit));

        await workOrders.PutOnHoldAsync(order.WorkOrderId, "Awaiting access");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workOrders.StartAsync(order.WorkOrderId));
        await workOrders.ResumeAsync(order.WorkOrderId);
        var resumed = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(resumed);
        Assert.AreEqual(WorkOrderStatus.InProgress, resumed.Status);
    }

    [TestMethod]
    public async Task PreventivePlanGenerationIsIdempotentWhileOpenAndCompletionAdvancesScheduleOnce()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out _, out var workOrders);
        var plans = new MaintenancePlanService(factory);
        var due = DateOnly.FromDateTime(DateTime.Today);
        var plan = await plans.CreateAsync(new MaintenancePlanDraft(
            null, "Main Site", "Plant Room", "Quarterly AC service", "HVAC",
            MaintenanceType.PreventiveMaintenance, "Inspect and service unit", 3,
            MaintenanceFrequencyUnit.Months, due, "Tech", 50m, true));

        var first = await plans.GenerateWorkOrderAsync(plan.MaintenancePlanId);
        var second = await plans.GenerateWorkOrderAsync(plan.MaintenancePlanId);
        Assert.AreEqual(first.WorkOrderId, second.WorkOrderId);
        await using (var beforeCompletion = factory.CreateMasterDbContext())
        {
            Assert.AreEqual(1, await beforeCompletion.WorkOrders.CountAsync(item => item.MaintenancePlanId == plan.MaintenancePlanId
                && item.Status != WorkOrderStatus.Completed && item.Status != WorkOrderStatus.Cancelled));
        }

        await workOrders.CompleteAsync(CompletionFor(first.WorkOrderId));
        await workOrders.CompleteAsync(CompletionFor(first.WorkOrderId));

        await using var master = factory.CreateMasterDbContext();
        var updated = await master.MaintenancePlans.AsNoTracking().SingleAsync(item => item.MaintenancePlanId == plan.MaintenancePlanId);
        Assert.IsNotNull(updated.LastCompletedDate);
        var expectedNext = updated.LastCompletedDate.Value.AddMonths(plan.FrequencyValue);
        Assert.AreEqual(expectedNext, updated.NextDueDate);
        Assert.AreEqual(0, await master.WorkOrders.CountAsync(item => item.MaintenancePlanId == plan.MaintenancePlanId
            && item.Status != WorkOrderStatus.Completed && item.Status != WorkOrderStatus.Cancelled));
    }

    [TestMethod]
    public async Task ConcurrentPreventiveGenerationConvergesToOneOpenWorkOrderAndOnePlanAudit()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out _, out _);
        var plans = new MaintenancePlanService(factory);
        var plan = await plans.CreateAsync(new MaintenancePlanDraft(
            null, "Main Site", "Plant Room", "Concurrent AC service", "HVAC",
            MaintenanceType.PreventiveMaintenance, "Inspect unit", 1,
            MaintenanceFrequencyUnit.Months, DateOnly.FromDateTime(DateTime.Today), "Tech", 25m, true));

        var generated = await Task.WhenAll(
            plans.GenerateWorkOrderAsync(plan.MaintenancePlanId),
            plans.GenerateWorkOrderAsync(plan.MaintenancePlanId));

        Assert.AreEqual(generated[0].WorkOrderId, generated[1].WorkOrderId);
        await using var master = factory.CreateMasterDbContext();
        Assert.AreEqual(1, await master.WorkOrders.CountAsync(item => item.MaintenancePlanId == plan.MaintenancePlanId
            && item.Status != WorkOrderStatus.Completed && item.Status != WorkOrderStatus.Cancelled));
        Assert.AreEqual(1, await master.MaintenancePlanAudit.CountAsync(item =>
            item.MaintenancePlanId == plan.MaintenancePlanId && item.Action == "WorkOrderGenerated"));
    }

    [TestMethod]
    public async Task WorkOrderCompletionAssetStateChangeHasAuditEvidence()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out _, out var workOrders);
        var assets = new AssetService(factory);
        var asset = await assets.AddAsync(new AssetDraft(
            "A-001", "Main Site", "Plant Room", "Air conditioner", "HVAC", "Maker", "", "Model", "SN1",
            "Good", "In Service", "", null, null, null, null, ""));

        var order = await workOrders.CreateAsync(WorkOrderDraftFor(asset.AssetId, assignedTo: "Tech"));
        await workOrders.CompleteAsync(CompletionFor(order.WorkOrderId) with
        {
            ConditionAfter = "Excellent",
            OperationalStatusAfter = "In Service"
        });

        var updated = await assets.GetByIdAsync(asset.AssetId);
        Assert.IsNotNull(updated);
        Assert.AreEqual("Excellent", updated.Condition);

        await using var master = factory.CreateMasterDbContext();
        Assert.IsTrue(await master.AssetAudit.AsNoTracking().AnyAsync(item =>
            item.AssetId == asset.AssetId && item.Action == "WorkOrderCompletionStateChange"));
    }

    [TestMethod]
    public async Task DashboardAndCrossYearQueriesUseCanonicalAnnualDataAndExcludeInvalidTotals()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out var maintenance, out _);
        var currentYear = DateTimeOffset.Now.Year;
        var priorYear = currentYear - 1;

        var current = await maintenance.CreateAsync(MaintenanceDraft(new DateTimeOffset(currentYear, DateTime.Today.Month, 2, 10, 0, 0, TimeSpan.Zero), 10m));
        var invalid = await maintenance.CreateAsync(MaintenanceDraft(new DateTimeOffset(currentYear, DateTime.Today.Month, 3, 10, 0, 0, TimeSpan.Zero), 100m));
        await maintenance.MarkInvalidAsync(invalid.MaintenanceRecordId, invalid.ReferenceYear, "duplicate");
        await maintenance.CreateAsync(MaintenanceDraft(new DateTimeOffset(priorYear, 12, 31, 10, 0, 0, TimeSpan.Zero), 20m));

        var dashboard = await maintenance.GetDashboardSummaryAsync(currentYear);
        Assert.IsTrue(dashboard.MonthCount >= 1);
        Assert.IsTrue(dashboard.MonthCost >= current.Cost.GetValueOrDefault());

        var aggregate = await maintenance.GetAggregateAsync(new MaintenanceQuery(Year: currentYear, IncludeInvalid: false));
        Assert.AreEqual(1L, aggregate.Count);
        Assert.AreEqual(10m, aggregate.TotalCost);

        var allYears = await maintenance.GetPageAsync(new MaintenanceQuery(IncludeInvalid: true, PageSize: 2));
        Assert.AreEqual(2, allYears.Items.Count);
        Assert.IsTrue(allYears.HasMore);
        Assert.IsNotNull(allYears.NextCursor);
        var next = await maintenance.GetPageAsync(new MaintenanceQuery(IncludeInvalid: true, PageSize: 2, Cursor: allYears.NextCursor));
        Assert.AreEqual(0, allYears.Items.Select(item => item.MaintenanceRecordId).Intersect(next.Items.Select(item => item.MaintenanceRecordId)).Count());
    }

    [TestMethod]
    public async Task HealthyBackupCanRestoreOverCorruptCurrentDatabaseAndTamperedBackupIsRejected()
    {
        using var scope = new TestDataScope();
        var factory = new DatabaseContextFactory(scope.Paths);
        using var initializer = new DatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var health = new DatabaseHealthService(scope.Paths);
        using var backup = new BackupService(scope.Paths, health, initializer);

        var healthyPath = Path.Combine(scope.Paths.BackupsDirectory, "healthy.zip");
        await backup.CreateBackupAsync(healthyPath);

        File.WriteAllBytes(scope.Paths.MasterDatabasePath, [1, 2, 3, 4, 5]);
        var restore = await backup.RestoreBackupAsync(healthyPath);
        Assert.AreEqual(Path.GetFullPath(healthyPath), restore.RestoredFrom);
        await health.ValidateCurrentDataAsync();

        var tamperedPath = Path.Combine(scope.Paths.BackupsDirectory, "tampered.zip");
        File.Copy(healthyPath, tamperedPath, overwrite: true);
        using (var archive = ZipFile.Open(tamperedPath, ZipArchiveMode.Update))
        {
            var target = archive.Entries.First(entry => entry.FullName.EndsWith("MaintenanceManager_Master.db", StringComparison.Ordinal));
            var name = target.FullName;
            target.Delete();
            var replacement = archive.CreateEntry(name);
            await using var output = replacement.Open();
            await output.WriteAsync(new byte[] { 9, 9, 9, 9 });
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backup.RestoreBackupAsync(tamperedPath));
        await health.ValidateCurrentDataAsync();
    }

    [TestMethod]
    public async Task WorkOrderPagingIsBoundedAndKeysetPagesDoNotOverlap()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out _, out var workOrders);

        for (var index = 0; index < 25; index++)
        {
            await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech") with
            {
                Title = $"Paged WO {index:D2}",
                ProblemDescription = $"Paged request {index:D2}"
            });
        }

        var first = await workOrders.GetPageAsync(new WorkOrderQuery(PageSize: 10));
        Assert.AreEqual(10, first.Items.Count);
        Assert.IsTrue(first.HasMore);
        Assert.IsNotNull(first.NextCursor);

        var second = await workOrders.GetPageAsync(new WorkOrderQuery(PageSize: 10, Cursor: first.NextCursor));
        Assert.AreEqual(10, second.Items.Count);
        Assert.AreEqual(0, first.Items.Select(item => item.WorkOrderId).Intersect(second.Items.Select(item => item.WorkOrderId)).Count());

        var oversizedRequest = await workOrders.GetPageAsync(new WorkOrderQuery(PageSize: 1000));
        Assert.IsTrue(oversizedRequest.Items.Count <= 200);
    }

    [TestMethod]
    public async Task WorkOrderFiltersAndDashboardMetricsAreEvaluatedInDatabase()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out _, out var workOrders);
        var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        var tomorrow = DateOnly.FromDateTime(DateTime.Today).AddDays(1);

        var urgent = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech A") with
        {
            Priority = WorkOrderPriority.Urgent,
            DueDate = yesterday,
            Location = "Plant A",
            Title = "Urgent pump repair"
        });
        await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech B") with
        {
            Priority = WorkOrderPriority.Normal,
            DueDate = tomorrow,
            Location = "Plant B",
            Title = "Routine inspection"
        });
        var cancelled = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech C") with
        {
            Priority = WorkOrderPriority.Urgent,
            DueDate = yesterday,
            Location = "Plant C",
            Title = "Cancelled repair"
        });
        await workOrders.CancelAsync(cancelled.WorkOrderId, "Not required");

        var metrics = await workOrders.GetDashboardMetricsAsync();
        Assert.AreEqual(2L, metrics.OpenCount);
        Assert.AreEqual(1L, metrics.UrgentOpenCount);
        Assert.AreEqual(1L, metrics.OverdueOpenCount);

        var filtered = await workOrders.GetPageAsync(new WorkOrderQuery(
            Priority: WorkOrderPriority.Urgent,
            Location: "Plant A",
            IsClosed: false,
            SearchText: "pump",
            PageSize: 50));
        Assert.AreEqual(1, filtered.Items.Count);
        Assert.AreEqual(urgent.WorkOrderId, filtered.Items[0].WorkOrderId);
    }

    [TestMethod]
    public async Task WorkOrderQueryProjectionRemainsConsistentAcrossLifecycleChanges()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out _, out var workOrders);
        var dueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        var created = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: "Tech") with { DueDate = dueDate });

        Assert.IsTrue(created.ReferenceYear >= 1900);
        Assert.IsTrue(created.ReferenceSequence > 0);
        Assert.AreEqual(created.ReportedAtUtc.UtcTicks, created.ReportedAtUtcTicks);
        Assert.AreEqual(dueDate.DayNumber, created.SortDueDateOrdinal);
        Assert.IsFalse(created.IsClosed);

        await workOrders.StartAsync(created.WorkOrderId);
        await workOrders.PutOnHoldAsync(created.WorkOrderId, "Waiting for access");
        await workOrders.ResumeAsync(created.WorkOrderId);
        await workOrders.CompleteAsync(CompletionFor(created.WorkOrderId));

        var completed = await workOrders.GetByIdAsync(created.WorkOrderId);
        Assert.IsNotNull(completed);
        Assert.IsTrue(completed.IsClosed);
        Assert.AreEqual(WorkOrderStatus.Completed, completed.Status);
        Assert.AreEqual(created.ReferenceYear, completed.ReferenceYear);
        Assert.AreEqual(created.ReferenceSequence, completed.ReferenceSequence);
        Assert.IsTrue(completed.ActivityAtUtcTicks >= created.ActivityAtUtcTicks);
    }

    [TestMethod]
    public async Task PendingMaintenanceOrderCanCompleteWithoutAssignmentAndCreatesCanonicalMaintenance()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out _, out var maintenance, out var workOrders);

        var order = await workOrders.CreateAsync(WorkOrderDraftFor(assignedTo: string.Empty) with
        {
            MaintenanceCategory = "HVAC",
            MaintenanceType = MaintenanceType.GeneralMaintenance
        });

        Assert.AreEqual(WorkOrderStatus.New, order.Status);
        Assert.IsFalse(order.IsClosed);

        await workOrders.CompleteAsync(CompletionFor(order.WorkOrderId));

        var completed = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(completed);
        Assert.AreEqual(WorkOrderStatus.Completed, completed.Status);
        Assert.IsTrue(completed.IsClosed);
        Assert.AreEqual(DateTimeOffset.Now.Year, completed.GeneratedMaintenanceYear);

        var canonical = await maintenance.GetByNumberAsync(completed.GeneratedMaintenanceNumber);
        Assert.IsNotNull(canonical);
        Assert.AreEqual(order.WorkOrderId, canonical.GeneratedByWorkOrderId);
        Assert.AreEqual("HVAC", canonical.MaintenanceType);
        var pendingAfterCompletion = await workOrders.GetPageAsync(new WorkOrderQuery(IsClosed: false, PageSize: 200));
        Assert.IsFalse(pendingAfterCompletion.Items.Any(item => item.WorkOrderId == order.WorkOrderId));
    }

    [TestMethod]
    public async Task PriorYearPendingOrderRemainsSingleMasterOrderAndCompletesIntoCurrentAnnualPartition()
    {
        using var scope = new TestDataScope();
        using var initializer = CreateInitialized(scope, out var factory, out var maintenance, out var workOrders);
        var currentYear = DateTimeOffset.Now.Year;
        var priorYear = currentYear - 1;

        var reportedAtUtc = new DateTimeOffset(priorYear, 12, 31, 23, 50, 0, TimeSpan.Zero);
        var dueDate = DateOnly.FromDateTime(reportedAtUtc.UtcDateTime);
        var order = new WorkOrder
        {
            WorkOrderId = Guid.NewGuid(),
            WorkOrderNumber = $"WO-{priorYear:0000}-999998",
            ReferenceYear = priorYear,
            ReferenceSequence = 999998,
            Site = "Main Site",
            Location = "Plant Room",
            Title = "Fix AC",
            MaintenanceCategory = "HVAC",
            MaintenanceType = MaintenanceType.CorrectiveMaintenance,
            Priority = WorkOrderPriority.Normal,
            ProblemDescription = "Unit is not cooling",
            RequestedBy = "Ops",
            ReportedAtUtc = reportedAtUtc,
            ReportedAtUtcTicks = reportedAtUtc.UtcTicks,
            DueDate = dueDate,
            Status = WorkOrderStatus.New,
            AssignedTo = string.Empty,
            CreatedBy = "Test",
            CreatedAtUtc = reportedAtUtc,
            Revision = 1,
            ActivityAtUtcTicks = reportedAtUtc.UtcTicks,
            IsClosed = false,
            SortDueDateOrdinal = dueDate.DayNumber
        };
        await using (var master = factory.CreateMasterDbContext())
        {
            master.WorkOrders.Add(order);
            await master.SaveChangesAsync();
        }

        var pendingPage = await workOrders.GetPageAsync(new WorkOrderQuery(IsClosed: false, PageSize: 200));
        Assert.IsTrue(pendingPage.Items.Any(item => item.WorkOrderId == order.WorkOrderId));

        await workOrders.CompleteAsync(CompletionFor(order.WorkOrderId));

        var completed = await workOrders.GetByIdAsync(order.WorkOrderId);
        Assert.IsNotNull(completed);
        Assert.AreEqual(priorYear, completed.ReferenceYear, "The order keeps its original creation/reference year for audit identity.");
        Assert.AreEqual(currentYear, completed.GeneratedMaintenanceYear, "Completion year owns the canonical Maintenance record.");
        StringAssert.StartsWith(completed.GeneratedMaintenanceNumber, $"MNT-{currentYear:0000}-");

        var canonical = await maintenance.GetByNumberAsync(completed.GeneratedMaintenanceNumber);
        Assert.IsNotNull(canonical);
        Assert.AreEqual(currentYear, canonical.ReferenceYear);
        Assert.AreEqual(order.WorkOrderId, canonical.GeneratedByWorkOrderId);

        await using var verification = factory.CreateMasterDbContext();
        Assert.AreEqual(1, await verification.WorkOrders.CountAsync(item => item.WorkOrderId == order.WorkOrderId));
        var pendingAfterCompletion = await workOrders.GetPageAsync(new WorkOrderQuery(IsClosed: false, PageSize: 200));
        Assert.IsFalse(pendingAfterCompletion.Items.Any(item => item.WorkOrderId == order.WorkOrderId));
    }

    [TestMethod]
    public void UserSettingsPersistAtomicallyAndAllFeatureValuesRoundTripImmediately()
    {
        using var scope = new TestDataScope();
        var service = new UserSettingsService(scope.Paths);

        service.SaveLanguageCode("ar");
        service.SaveWorkOrdersEnabled(false);
        service.SavePreventiveMaintenanceEnabled(false);
        service.SaveAssetTrackingEnabled(true);
        service.SaveCustomMaintenanceTypes(["Electrical", "HVAC", "Electrical"]);

        Assert.AreEqual("ar", service.LoadLanguageCode());
        Assert.IsFalse(service.LoadWorkOrdersEnabled());
        Assert.IsFalse(service.LoadPreventiveMaintenanceEnabled());
        Assert.IsTrue(service.LoadAssetTrackingEnabled());
        CollectionAssert.AreEqual(ExpectedCustomMaintenanceTypes, service.LoadCustomMaintenanceTypes().ToArray());
    }

    private static DatabaseInitializer CreateInitialized(
        TestDataScope scope,
        out DatabaseContextFactory factory,
        out MaintenanceRecordService maintenance,
        out WorkOrderService workOrders)
    {
        factory = new DatabaseContextFactory(scope.Paths);
        var initializer = new DatabaseInitializer(factory);
        initializer.InitializeAsync().GetAwaiter().GetResult();
        var annual = new AnnualMaintenanceDatabaseManager(scope.Paths, factory, initializer);
        maintenance = new MaintenanceRecordService(factory, annual);
        workOrders = new WorkOrderService(factory, maintenance);
        return initializer;
    }

    private static WorkOrderDraft WorkOrderDraftFor(Guid? assetId = null, string assignedTo = "Tech") => new(
        assetId, null, "Main Site", "Plant Room", "Fix AC", "HVAC",
        MaintenanceType.CorrectiveMaintenance, WorkOrderPriority.Normal,
        "Unit is not cooling", "Ops", DateOnly.FromDateTime(DateTime.Today), assignedTo, "");

    private static WorkOrderCompletionDraft CompletionFor(Guid workOrderId) => new(
        workOrderId, "Replaced failed relay", "Tech", 25m, 1m, "Relay", "Good", "In Service", "Completed normally");

    private static MaintenanceRecordDraft MaintenanceDraft(DateTimeOffset occurredAt, decimal cost) => new(
        occurredAt, "Main Site", "Plant Room", "Maintenance", "Repair", "Completed", "Tech", cost, "", null);
}
