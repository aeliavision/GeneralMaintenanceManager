using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

public sealed record BackupProgress(
    BackupProgressStage Stage,
    int Percent,
    int ProcessedItems = 0,
    int TotalItems = 0);
