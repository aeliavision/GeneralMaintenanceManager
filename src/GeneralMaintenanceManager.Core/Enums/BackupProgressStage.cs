namespace GeneralMaintenanceManager.Core.Enums;

public enum BackupProgressStage
{
    Preparing,
    ValidatingCurrentData,
    SnapshottingData,
    ValidatingSnapshot,
    CompressingBackup,
    FinalizingBackup,
    BackupCompleted,
    CreatingSafetyBackup,
    OpeningBackup,
    ExtractingBackup,
    ValidatingBackup,
    ReplacingData,
    InitializingDatabase,
    VerifyingRestoredData,
    RestoreCompleted
}
