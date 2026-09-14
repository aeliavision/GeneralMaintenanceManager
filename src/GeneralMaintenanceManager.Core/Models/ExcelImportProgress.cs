using GeneralMaintenanceManager.Core.Enums;

namespace GeneralMaintenanceManager.Core.Models;

public sealed record ExcelImportProgress(
    ExcelImportStage Stage,
    int Percent,
    int ProcessedRows = 0,
    int TotalRows = 0);
