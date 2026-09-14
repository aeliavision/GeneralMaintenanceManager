using GeneralMaintenanceManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public sealed class MasterDbContext(DbContextOptions<MasterDbContext> options, IDisposable? databaseLease = null) : DbContext(options)
{
    private IDisposable? _databaseLease = databaseLease;
    public DbSet<Asset> Asset => Set<Asset>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();
    public DbSet<AssetAudit> AssetAudit => Set<AssetAudit>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderAudit> WorkOrderAudit => Set<WorkOrderAudit>();
    public DbSet<MaintenancePlan> MaintenancePlans => Set<MaintenancePlan>();
    public DbSet<MaintenancePlanAudit> MaintenancePlanAudit => Set<MaintenancePlanAudit>();
    public DbSet<WorkOrderCompletionIntent> WorkOrderCompletionIntents => Set<WorkOrderCompletionIntent>();
    public DbSet<ActivityHistoryEntry> ActivityHistory => Set<ActivityHistoryEntry>();
    internal DbSet<SchemaInfoRow> SchemaInfo => Set<SchemaInfoRow>();
    internal DbSet<SchemaMigrationJournalRow> SchemaMigrationJournal => Set<SchemaMigrationJournalRow>();
    internal DbSet<MaintenanceNumberSequenceRow> MaintenanceNumberSequence => Set<MaintenanceNumberSequenceRow>();
    internal DbSet<WorkOrderNumberSequenceRow> WorkOrderNumberSequence => Set<WorkOrderNumberSequenceRow>();
    internal DbSet<MaintenancePlanNumberSequenceRow> MaintenancePlanNumberSequence => Set<MaintenancePlanNumberSequenceRow>();

    public override void Dispose()
    {
        base.Dispose();
        Interlocked.Exchange(ref _databaseLease, null)?.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _databaseLease, null)?.Dispose();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSchemaInfo(modelBuilder);
        ConfigureMigrationJournal(modelBuilder);
        ConfigureReferenceSequences(modelBuilder);

        var asset = modelBuilder.Entity<Asset>();
        asset.ToTable("Asset", table =>
        {
            table.HasCheckConstraint("CK_Asset_PurchasePriceMinorUnits", "\"PurchasePriceMinorUnits\" IS NULL OR \"PurchasePriceMinorUnits\" >= 0");
        });
        asset.HasKey(item => item.AssetId);
        asset.Property(item => item.AssetId).ValueGeneratedNever();
        asset.Property(item => item.AssetNumber).HasMaxLength(64).UseCollation("NOCASE");
        asset.Property(item => item.ImportedAssetNumber).HasMaxLength(64);
        asset.Property(item => item.AssetName).HasMaxLength(256).IsRequired();
        asset.Property(item => item.AssetCategory).HasMaxLength(128);
        asset.Property(item => item.Site).HasMaxLength(128);
        asset.Property(item => item.Location).HasMaxLength(128);
        asset.Property(item => item.Manufacturer).HasMaxLength(128);
        asset.Property(item => item.Country).HasMaxLength(64);
        asset.Property(item => item.Model).HasMaxLength(128);
        asset.Property(item => item.SerialNumber).HasMaxLength(128);
        asset.Property(item => item.Condition).HasMaxLength(64);
        asset.Property(item => item.OperationalStatus).HasMaxLength(64);
        asset.Property(item => item.TechnicalSpecification).HasMaxLength(1000);
        asset.Property(item => item.PurchasePrice)
            .HasConversion(ExactNumericStorage.MoneyConverter)
            .HasColumnName("PurchasePriceMinorUnits")
            .HasColumnType("INTEGER");
        asset.Property(item => item.Notes).HasMaxLength(4000);
        asset.Property(item => item.SourceSheet).HasMaxLength(128);
        asset.HasIndex(item => item.AssetNumber)
            .IsUnique()
            .HasFilter("\"AssetNumber\" <> '' AND \"IsArchived\" = 0");
        asset.HasIndex(item => new { item.SourceSheet, item.SourceRow });
        asset.HasIndex(item => item.AssetName);
        asset.HasIndex(item => item.SerialNumber);
        asset.HasIndex(item => new { item.Site, item.Location });
        asset.HasIndex(item => item.AssetCategory);
        asset.HasIndex(item => item.OperationalStatus);
        asset.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(item => item.MergedIntoAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        var review = modelBuilder.Entity<ReviewItem>();
        review.ToTable("ReviewItems");
        review.HasKey(item => item.ReviewId);
        review.Property(item => item.ReviewId).ValueGeneratedNever();
        review.Property(item => item.ReviewType).HasMaxLength(64).IsRequired();
        review.Property(item => item.Reasons).HasMaxLength(4000);
        review.Property(item => item.Status).HasMaxLength(32).IsRequired();
        review.Property(item => item.Resolution).HasMaxLength(256);
        review.Property(item => item.ResolvedBy).HasMaxLength(256);
        review.HasIndex(item => item.Status);
        review.HasIndex(item => new { item.AssetIdA, item.AssetIdB });
        review.HasOne<Asset>().WithMany().HasForeignKey(item => item.AssetIdA).OnDelete(DeleteBehavior.Restrict);
        review.HasOne<Asset>().WithMany().HasForeignKey(item => item.AssetIdB).OnDelete(DeleteBehavior.Restrict);

        var audit = modelBuilder.Entity<AssetAudit>();
        audit.ToTable("AssetAudit");
        audit.HasKey(item => item.AuditId);
        audit.Property(item => item.AuditId).ValueGeneratedNever();
        audit.Property(item => item.Action).HasMaxLength(64).IsRequired();
        audit.Property(item => item.Reason).HasMaxLength(1000);
        audit.Property(item => item.Changes).HasMaxLength(12000);
        audit.Property(item => item.ChangedBy).HasMaxLength(256);
        audit.HasIndex(item => new { item.AssetId, item.ChangedAtUtc });
        audit.HasOne<Asset>().WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.Restrict);

        var workOrder = modelBuilder.Entity<WorkOrder>();
        workOrder.ToTable("WorkOrders", table =>
        {
            table.HasCheckConstraint("CK_WorkOrders_CostMinorUnits", "\"CostMinorUnits\" IS NULL OR \"CostMinorUnits\" >= 0");
            table.HasCheckConstraint("CK_WorkOrders_DowntimeMinutes", "\"DowntimeMinutes\" IS NULL OR \"DowntimeMinutes\" >= 0");
            table.HasCheckConstraint("CK_WorkOrders_ReferenceYear", "\"ReferenceYear\" >= 1900 AND \"ReferenceYear\" <= 9999");
            table.HasCheckConstraint("CK_WorkOrders_ReferenceSequence", "\"ReferenceSequence\" >= 1 AND \"ReferenceSequence\" <= 999999");
            table.HasCheckConstraint(
                "CK_WorkOrders_ReferenceNumberConsistency",
                "\"WorkOrderNumber\" COLLATE BINARY = printf('WO-%04d-%06d', \"ReferenceYear\", \"ReferenceSequence\")");
        });
        workOrder.HasKey(item => item.WorkOrderId);
        workOrder.Property(item => item.WorkOrderId).ValueGeneratedNever();
        workOrder.Property(item => item.WorkOrderNumber).HasMaxLength(32).UseCollation("NOCASE").IsRequired();
        workOrder.Property(item => item.AssetNumber).HasMaxLength(64);
        workOrder.Property(item => item.GeneratedMaintenanceNumber).HasMaxLength(32).UseCollation("NOCASE");
        workOrder.Property(item => item.Site).HasMaxLength(128);
        workOrder.Property(item => item.Location).HasMaxLength(128);
        workOrder.Property(item => item.Title).HasMaxLength(256).IsRequired();
        workOrder.Property(item => item.MaintenanceCategory).HasMaxLength(128);
        workOrder.Property(item => item.ProblemDescription).HasMaxLength(4000);
        workOrder.Property(item => item.Notes).HasMaxLength(4000);
        workOrder.Property(item => item.RequestedBy).HasMaxLength(256);
        workOrder.Property(item => item.AssignedTo).HasMaxLength(256);
        workOrder.Property(item => item.WorkPerformed).HasMaxLength(4000);
        workOrder.Property(item => item.PerformedBy).HasMaxLength(256);
        workOrder.Property(item => item.Cost)
            .HasConversion(ExactNumericStorage.MoneyConverter)
            .HasColumnName("CostMinorUnits")
            .HasColumnType("INTEGER");
        workOrder.Property(item => item.DowntimeHours)
            .HasConversion(ExactNumericStorage.HoursToMinutesConverter)
            .HasColumnName("DowntimeMinutes")
            .HasColumnType("INTEGER");
        workOrder.Property(item => item.MaterialsOrPartsNotes).HasMaxLength(4000);
        workOrder.Property(item => item.ConditionAfter).HasMaxLength(64);
        workOrder.Property(item => item.OperationalStatusAfter).HasMaxLength(64);
        workOrder.Property(item => item.CompletionNotes).HasMaxLength(4000);
        workOrder.Property(item => item.CreatedBy).HasMaxLength(256);
        workOrder.Property(item => item.LastModifiedBy).HasMaxLength(256);
        workOrder.HasIndex(item => item.WorkOrderNumber).IsUnique();
        workOrder.HasIndex(item => new { item.ReferenceYear, item.ReferenceSequence }).IsUnique();
        workOrder.HasIndex(item => item.Status);
        workOrder.HasIndex(item => item.Priority);
        workOrder.HasIndex(item => item.AssetId);
        workOrder.HasIndex(item => item.MaintenancePlanId)
            .IsUnique()
            .HasFilter("\"MaintenancePlanId\" IS NOT NULL AND \"Status\" NOT IN (5, 6)");
        workOrder.HasIndex(item => item.GeneratedMaintenanceRecordId)
            .IsUnique()
            .HasFilter("\"GeneratedMaintenanceRecordId\" IS NOT NULL");
        workOrder.HasIndex(item => item.GeneratedMaintenanceNumber)
            .IsUnique()
            .HasFilter("\"GeneratedMaintenanceNumber\" <> ''");
        workOrder.HasIndex(item => new { item.Site, item.Location });
        workOrder.HasIndex(item => item.DueDate);
        workOrder.HasIndex(item => new { item.IsClosed, item.Priority, item.SortDueDateOrdinal, item.ReportedAtUtcTicks, item.ReferenceSequence })
            .IsDescending(false, true, false, true, true);
        workOrder.HasIndex(item => new { item.Status, item.IsClosed, item.Priority, item.SortDueDateOrdinal, item.ReportedAtUtcTicks, item.ReferenceSequence })
            .IsDescending(false, false, true, false, true, true);
        workOrder.HasIndex(item => new { item.Location, item.IsClosed, item.Priority, item.SortDueDateOrdinal, item.ReportedAtUtcTicks, item.ReferenceSequence })
            .IsDescending(false, false, true, false, true, true);
        workOrder.HasIndex(item => new { item.AssignedTo, item.IsClosed, item.Priority, item.SortDueDateOrdinal, item.ReportedAtUtcTicks, item.ReferenceSequence })
            .IsDescending(false, false, true, false, true, true);
        workOrder.HasIndex(item => new { item.ReferenceYear, item.ActivityAtUtcTicks, item.ReferenceSequence })
            .IsDescending(false, true, true);
        workOrder.HasOne<Asset>().WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.Restrict);
        workOrder.HasOne<MaintenancePlan>().WithMany().HasForeignKey(item => item.MaintenancePlanId).OnDelete(DeleteBehavior.Restrict);
        // GeneratedMaintenanceRecordId points into an annual database, so no cross-file FK is declared.

        var completionIntent = modelBuilder.Entity<WorkOrderCompletionIntent>();
        completionIntent.ToTable("WorkOrderCompletionIntents", table =>
        {
            table.HasCheckConstraint("CK_WorkOrderCompletionIntents_ReferenceYear", "\"ReferenceYear\" >= 1900 AND \"ReferenceYear\" <= 9999");
            table.HasCheckConstraint("CK_WorkOrderCompletionIntents_ReferenceSequence", "\"ReferenceSequence\" >= 1 AND \"ReferenceSequence\" <= 9999999");
            table.HasCheckConstraint("CK_WorkOrderCompletionIntents_CostMinorUnits", "\"CostMinorUnits\" IS NULL OR \"CostMinorUnits\" >= 0");
            table.HasCheckConstraint("CK_WorkOrderCompletionIntents_DowntimeMinutes", "\"DowntimeMinutes\" IS NULL OR \"DowntimeMinutes\" >= 0");
        });
        completionIntent.HasKey(item => item.WorkOrderCompletionIntentId);
        completionIntent.Property(item => item.WorkOrderCompletionIntentId).ValueGeneratedNever();
        completionIntent.Property(item => item.MaintenanceNumber).HasMaxLength(32).UseCollation("NOCASE").IsRequired();
        completionIntent.Property(item => item.Site).HasMaxLength(128);
        completionIntent.Property(item => item.Location).HasMaxLength(128).IsRequired();
        completionIntent.Property(item => item.Subject).HasMaxLength(256).IsRequired();
        completionIntent.Property(item => item.MaintenanceType).HasMaxLength(128).IsRequired();
        completionIntent.Property(item => item.AssetNumberSnapshot).HasMaxLength(64);
        completionIntent.Property(item => item.AssetNameSnapshot).HasMaxLength(256);
        completionIntent.Property(item => item.WorkOrderNumberSnapshot).HasMaxLength(32).IsRequired();
        completionIntent.Property(item => item.WorkPerformed).HasMaxLength(4000).IsRequired();
        completionIntent.Property(item => item.PerformedBy).HasMaxLength(256);
        completionIntent.Property(item => item.Cost)
            .HasConversion(ExactNumericStorage.MoneyConverter)
            .HasColumnName("CostMinorUnits")
            .HasColumnType("INTEGER");
        completionIntent.Property(item => item.DowntimeHours)
            .HasConversion(ExactNumericStorage.HoursToMinutesConverter)
            .HasColumnName("DowntimeMinutes")
            .HasColumnType("INTEGER");
        completionIntent.Property(item => item.MaterialsOrPartsNotes).HasMaxLength(4000);
        completionIntent.Property(item => item.ConditionAfter).HasMaxLength(64);
        completionIntent.Property(item => item.OperationalStatusAfter).HasMaxLength(64);
        completionIntent.Property(item => item.CompletionNotes).HasMaxLength(4000);
        completionIntent.Property(item => item.MaintenanceNotes).HasMaxLength(8000);
        completionIntent.Property(item => item.State).HasMaxLength(32).IsRequired();
        completionIntent.HasIndex(item => item.WorkOrderId).IsUnique();
        completionIntent.HasIndex(item => item.MaintenanceRecordId).IsUnique();
        completionIntent.HasIndex(item => item.MaintenanceNumber).IsUnique();
        completionIntent.HasIndex(item => item.State);
        completionIntent.HasOne<WorkOrder>().WithMany().HasForeignKey(item => item.WorkOrderId).OnDelete(DeleteBehavior.Restrict);

        var workOrderAudit = modelBuilder.Entity<WorkOrderAudit>();
        workOrderAudit.ToTable("WorkOrderAudit");
        workOrderAudit.HasKey(item => item.AuditId);
        workOrderAudit.Property(item => item.AuditId).ValueGeneratedNever();
        workOrderAudit.Property(item => item.Action).HasMaxLength(64).IsRequired();
        workOrderAudit.Property(item => item.Reason).HasMaxLength(1000);
        workOrderAudit.Property(item => item.Changes).HasMaxLength(12000);
        workOrderAudit.Property(item => item.ChangedBy).HasMaxLength(256);
        workOrderAudit.HasIndex(item => new { item.WorkOrderId, item.ChangedAtUtc });
        workOrderAudit.HasOne<WorkOrder>().WithMany().HasForeignKey(item => item.WorkOrderId).OnDelete(DeleteBehavior.Restrict);

        var plan = modelBuilder.Entity<MaintenancePlan>();
        plan.ToTable("MaintenancePlans", table =>
        {
            table.HasCheckConstraint("CK_MaintenancePlans_EstimatedCostMinorUnits", "\"EstimatedCostMinorUnits\" IS NULL OR \"EstimatedCostMinorUnits\" >= 0");
        });
        plan.HasKey(item => item.MaintenancePlanId);
        plan.Property(item => item.MaintenancePlanId).ValueGeneratedNever();
        plan.Property(item => item.MaintenancePlanNumber).HasMaxLength(32).UseCollation("NOCASE").IsRequired();
        plan.Property(item => item.AssetNumber).HasMaxLength(64);
        plan.Property(item => item.Site).HasMaxLength(128);
        plan.Property(item => item.Location).HasMaxLength(128);
        plan.Property(item => item.PlanName).HasMaxLength(256).IsRequired();
        plan.Property(item => item.MaintenanceCategory).HasMaxLength(128);
        plan.Property(item => item.Instructions).HasMaxLength(4000);
        plan.Property(item => item.AssignedTo).HasMaxLength(256);
        plan.Property(item => item.EstimatedCost)
            .HasConversion(ExactNumericStorage.MoneyConverter)
            .HasColumnName("EstimatedCostMinorUnits")
            .HasColumnType("INTEGER");
        plan.Property(item => item.Revision).HasDefaultValue(1);
        plan.HasIndex(item => item.MaintenancePlanNumber).IsUnique().HasFilter("\"MaintenancePlanNumber\" <> ''");
        plan.HasIndex(item => item.AssetId);
        plan.HasIndex(item => item.NextDueDate);
        plan.HasIndex(item => item.IsActive);
        plan.HasIndex(item => new { item.IsActive, item.NextDueDate });
        plan.HasOne<Asset>().WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.Restrict);

        var maintenancePlanAudit = modelBuilder.Entity<MaintenancePlanAudit>();
        maintenancePlanAudit.ToTable("MaintenancePlanAudit");
        maintenancePlanAudit.HasKey(item => item.AuditId);
        maintenancePlanAudit.Property(item => item.AuditId).ValueGeneratedNever();
        maintenancePlanAudit.Property(item => item.Action).HasMaxLength(64).IsRequired();
        maintenancePlanAudit.Property(item => item.Changes).HasMaxLength(12000);
        maintenancePlanAudit.Property(item => item.ChangedBy).HasMaxLength(256);
        maintenancePlanAudit.HasIndex(item => new { item.MaintenancePlanId, item.ChangedAtUtc });
        maintenancePlanAudit.HasOne<MaintenancePlan>().WithMany().HasForeignKey(item => item.MaintenancePlanId).OnDelete(DeleteBehavior.Restrict);

        var activityHistory = modelBuilder.Entity<ActivityHistoryEntry>();
        activityHistory.ToTable("ActivityHistory");
        activityHistory.HasKey(item => item.ActivityHistoryEntryId);
        activityHistory.Property(item => item.ActivityHistoryEntryId).ValueGeneratedNever();
        activityHistory.Property(item => item.ActivityType).HasMaxLength(64).IsRequired();
        activityHistory.Property(item => item.RecordType).HasMaxLength(64).IsRequired();
        activityHistory.Property(item => item.Reference).HasMaxLength(64);
        activityHistory.Property(item => item.Site).HasMaxLength(128);
        activityHistory.Property(item => item.Location).HasMaxLength(128);
        activityHistory.Property(item => item.Subject).HasMaxLength(256);
        activityHistory.Property(item => item.Description).HasMaxLength(4000);
        activityHistory.Property(item => item.ChangedBy).HasMaxLength(256);
        activityHistory.Property(item => item.Changes).HasMaxLength(12000);
        // Activity History is a read-heavy append-only timeline. Match indexes to the
        // bounded report query shapes: equality predicates first, newest-first range/order last.
        activityHistory.HasIndex(item => new { item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(true, true);
        activityHistory.HasIndex(item => new { item.RecordType, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, true, true);
        activityHistory.HasIndex(item => new { item.RecordType, item.RecordId, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, false, true, true);
        activityHistory.HasIndex(item => new { item.ActivityType, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, true, true);
        activityHistory.HasIndex(item => new { item.Site, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, true, true);
        activityHistory.HasIndex(item => new { item.Location, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, true, true);
        activityHistory.HasIndex(item => new { item.ChangedBy, item.OccurredAtUtcTicks, item.ActivityHistoryEntryId })
            .IsDescending(false, true, true);
        activityHistory.HasIndex(item => item.Reference);
    }

    private static void ConfigureSchemaInfo(ModelBuilder modelBuilder)
    {
        var schemaInfo = modelBuilder.Entity<SchemaInfoRow>();
        schemaInfo.ToTable("SchemaInfo", table =>
        {
            table.HasCheckConstraint("CK_SchemaInfo_Singleton", "\"Id\" = 1");
            table.HasCheckConstraint("CK_SchemaInfo_Version", "\"SchemaVersion\" >= 1");
            table.HasCheckConstraint("CK_SchemaInfo_MasterPartition", "\"PartitionYear\" IS NULL");
        });
        schemaInfo.HasKey(item => item.Id);
        schemaInfo.Property(item => item.Id).ValueGeneratedNever();
        schemaInfo.Property(item => item.FormatId).HasMaxLength(128).IsRequired();
        schemaInfo.Property(item => item.SchemaVersion).IsRequired();
        schemaInfo.Property(item => item.PartitionYear);
        schemaInfo.Property(item => item.CreatedAtUtc).IsRequired();
    }


    private static void ConfigureMigrationJournal(ModelBuilder modelBuilder)
    {
        var journal = modelBuilder.Entity<SchemaMigrationJournalRow>();
        journal.ToTable("SchemaMigrationJournal", table =>
        {
            table.HasCheckConstraint("CK_SchemaMigrationJournal_Versions", "\"FromVersion\" >= 1 AND \"ToVersion\" > \"FromVersion\"");
        });
        journal.HasKey(item => item.MigrationId);
        journal.Property(item => item.MigrationId).HasMaxLength(160).IsRequired();
        journal.Property(item => item.State).HasMaxLength(32).IsRequired();
        journal.Property(item => item.ProgressCursor).HasMaxLength(512).IsRequired();
        journal.Property(item => item.PreUpgradeBackupPath).HasMaxLength(2048).IsRequired();
        journal.Property(item => item.PreUpgradeBackupSha256).HasMaxLength(64).IsRequired();
        journal.Property(item => item.LastError).HasMaxLength(4000).IsRequired();
        journal.HasIndex(item => new { item.FromVersion, item.ToVersion });
        journal.HasIndex(item => item.State);
    }

    private static void ConfigureReferenceSequences(ModelBuilder modelBuilder)
    {
        var maintenance = modelBuilder.Entity<MaintenanceNumberSequenceRow>();
        maintenance.ToTable("MaintenanceNumberSequence", table =>
            table.HasCheckConstraint("CK_MaintenanceNumberSequence_LastValue", "\"LastValue\" >= 0 AND \"LastValue\" <= 9999999"));
        maintenance.HasKey(item => item.Year);
        maintenance.Property(item => item.Year).ValueGeneratedNever();
        maintenance.Property(item => item.LastValue).IsRequired();

        var workOrder = modelBuilder.Entity<WorkOrderNumberSequenceRow>();
        workOrder.ToTable("WorkOrderNumberSequence", table =>
            table.HasCheckConstraint("CK_WorkOrderNumberSequence_LastValue", "\"LastValue\" >= 0 AND \"LastValue\" <= 999999"));
        workOrder.HasKey(item => item.Year);
        workOrder.Property(item => item.Year).ValueGeneratedNever();
        workOrder.Property(item => item.LastValue).IsRequired();

        var plan = modelBuilder.Entity<MaintenancePlanNumberSequenceRow>();
        plan.ToTable("MaintenancePlanNumberSequence", table =>
            table.HasCheckConstraint("CK_MaintenancePlanNumberSequence_LastValue", "\"LastValue\" >= 0 AND \"LastValue\" <= 999999"));
        plan.HasKey(item => item.Year);
        plan.Property(item => item.Year).ValueGeneratedNever();
        plan.Property(item => item.LastValue).IsRequired();
    }
}
