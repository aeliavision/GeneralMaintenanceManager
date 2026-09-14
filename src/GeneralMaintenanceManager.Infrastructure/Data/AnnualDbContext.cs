using GeneralMaintenanceManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public sealed class AnnualDbContext(DbContextOptions<AnnualDbContext> options, IDisposable? databaseLease = null) : DbContext(options)
{
    private IDisposable? _databaseLease = databaseLease;
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<MaintenanceActivity> MaintenanceActivity => Set<MaintenanceActivity>();
    internal DbSet<SchemaInfoRow> SchemaInfo => Set<SchemaInfoRow>();
    internal DbSet<SchemaMigrationJournalRow> SchemaMigrationJournal => Set<SchemaMigrationJournalRow>();

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
        var schemaInfo = modelBuilder.Entity<SchemaInfoRow>();
        schemaInfo.ToTable("SchemaInfo", table =>
        {
            table.HasCheckConstraint("CK_SchemaInfo_Singleton", "\"Id\" = 1");
            table.HasCheckConstraint("CK_SchemaInfo_Version", "\"SchemaVersion\" >= 1");
            table.HasCheckConstraint("CK_SchemaInfo_AnnualPartition", "\"PartitionYear\" IS NOT NULL AND \"PartitionYear\" >= 1900 AND \"PartitionYear\" <= 9999");
        });
        schemaInfo.HasKey(item => item.Id);
        schemaInfo.Property(item => item.Id).ValueGeneratedNever();
        schemaInfo.Property(item => item.FormatId).HasMaxLength(128).IsRequired();
        schemaInfo.Property(item => item.SchemaVersion).IsRequired();
        schemaInfo.Property(item => item.PartitionYear).IsRequired();
        schemaInfo.Property(item => item.CreatedAtUtc).IsRequired();

        ConfigureMigrationJournal(modelBuilder);

        var record = modelBuilder.Entity<MaintenanceRecord>();
        record.ToTable("MaintenanceRecords", table =>
        {
            table.HasCheckConstraint("CK_MaintenanceRecords_ReferenceYear", "\"ReferenceYear\" >= 1900 AND \"ReferenceYear\" <= 9999");
            table.HasCheckConstraint("CK_MaintenanceRecords_Revision", "\"Revision\" >= 1");
            table.HasCheckConstraint("CK_MaintenanceRecords_ReferenceSequence", "\"ReferenceSequence\" >= 1 AND \"ReferenceSequence\" <= 9999999");
            table.HasCheckConstraint("CK_MaintenanceRecords_CostMinorUnits", "\"CostMinorUnits\" IS NULL OR \"CostMinorUnits\" >= 0");
            table.HasCheckConstraint("CK_MaintenanceRecords_DowntimeMinutes", "\"DowntimeMinutes\" IS NULL OR \"DowntimeMinutes\" >= 0");
        });
        record.HasKey(item => item.MaintenanceRecordId);
        record.Property(item => item.MaintenanceRecordId).ValueGeneratedNever();
        record.Property(item => item.MaintenanceNumber).HasMaxLength(32).UseCollation("NOCASE").IsRequired();
        record.Property(item => item.ReferenceYear).IsRequired();
        record.Property(item => item.ReferenceSequence).IsRequired();
        record.Property(item => item.OccurredAtUtcTicks).IsRequired();
        record.Property(item => item.Site).HasMaxLength(128);
        record.Property(item => item.Location).HasMaxLength(128).IsRequired();
        record.Property(item => item.Subject).HasMaxLength(256).IsRequired();
        record.Property(item => item.MaintenanceType).HasMaxLength(128).IsRequired();
        record.Property(item => item.WorkPerformed).HasMaxLength(4000).IsRequired();
        record.Property(item => item.PerformedBy).HasMaxLength(256);
        record.Property(item => item.Cost)
            .HasConversion(ExactNumericStorage.MoneyConverter)
            .HasColumnName("CostMinorUnits")
            .HasColumnType("INTEGER");
        record.Property(item => item.DowntimeHours)
            .HasConversion(ExactNumericStorage.HoursToMinutesConverter)
            .HasColumnName("DowntimeMinutes")
            .HasColumnType("INTEGER");
        record.Property(item => item.Notes).HasMaxLength(4000);
        record.Property(item => item.AssetNumberSnapshot).HasMaxLength(64);
        record.Property(item => item.AssetNameSnapshot).HasMaxLength(256);
        record.Property(item => item.WorkOrderNumberSnapshot).HasMaxLength(32);
        record.Property(item => item.CreatedAtUtcTicks).IsRequired();
        record.Property(item => item.CreatedBy).HasMaxLength(256).IsRequired();
        record.Property(item => item.Revision).HasDefaultValue(1);
        record.Property(item => item.InvalidReason).HasMaxLength(1000);

        record.HasIndex(item => item.MaintenanceNumber).IsUnique();
        record.HasIndex(item => new { item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.Location, item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.Site, item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.MaintenanceType, item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.AssetId, item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => new { item.PerformedBy, item.IsInvalid, item.OccurredAtUtcTicks, item.CreatedAtUtcTicks, item.ReferenceSequence });
        record.HasIndex(item => item.GeneratedByWorkOrderId)
            .IsUnique()
            .HasFilter("\"GeneratedByWorkOrderId\" IS NOT NULL");

        var activity = modelBuilder.Entity<MaintenanceActivity>();
        activity.ToTable("MaintenanceActivity");
        activity.HasKey(item => item.MaintenanceActivityId);
        activity.Property(item => item.MaintenanceActivityId).ValueGeneratedNever();
        activity.Property(item => item.ActivityType).HasMaxLength(64).IsRequired();
        activity.Property(item => item.Reason).HasMaxLength(1000);
        activity.Property(item => item.Changes).HasMaxLength(12000);
        activity.Property(item => item.ChangedBy).HasMaxLength(256).IsRequired();
        activity.HasIndex(item => new { item.MaintenanceRecordId, item.OccurredAtUtcTicks });
        activity.HasIndex(item => new { item.OccurredAtUtcTicks, item.MaintenanceActivityId });
        activity.HasIndex(item => new { item.ActivityType, item.OccurredAtUtcTicks, item.MaintenanceActivityId });
        activity.HasOne<MaintenanceRecord>()
            .WithMany()
            .HasForeignKey(item => item.MaintenanceRecordId)
            .OnDelete(DeleteBehavior.Restrict);
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
}
