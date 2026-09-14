namespace GeneralMaintenanceManager.Core.Models;

public sealed record RestoreResult(
    string RestoredFrom,
    string SafetyBackupPath);
