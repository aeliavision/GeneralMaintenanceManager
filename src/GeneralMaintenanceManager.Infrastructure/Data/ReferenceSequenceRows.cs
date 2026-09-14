namespace GeneralMaintenanceManager.Infrastructure.Data;

// These are persistence-only rows used so a fresh EF Core EnsureCreated database
// contains the reference allocators from the start. ReferenceNumberAllocator still
// performs the transactional increments with SQL.
internal sealed class MaintenanceNumberSequenceRow
{
    public int Year { get; set; }
    public long LastValue { get; set; }
}

internal sealed class WorkOrderNumberSequenceRow
{
    public int Year { get; set; }
    public long LastValue { get; set; }
}

internal sealed class MaintenancePlanNumberSequenceRow
{
    public int Year { get; set; }
    public long LastValue { get; set; }
}
