namespace GeneralMaintenanceManager.Core.Enums;

public enum ExcelImportStage
{
    OpeningWorkbook,
    ReadingRows,
    LoadingExistingRecords,
    UpdatingDatabase,
    RecordingHistory,
    WritingReviewLog,
    Completed
}
