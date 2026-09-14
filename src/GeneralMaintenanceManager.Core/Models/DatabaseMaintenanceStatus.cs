namespace GeneralMaintenanceManager.Core.Models;

public sealed record DatabaseMaintenanceStatus(
    int MasterSchemaVersion,
    int AnnualSchemaVersion,
    int AnnualDatabaseCount,
    string JournalMode,
    string SynchronousMode,
    long DatabaseBytes,
    long WalBytes,
    int PendingWorkOrderCompletions,
    long WorkOrderSearchRows,
    long WorkOrderRows,
    long PreventiveOccurrenceRows);
