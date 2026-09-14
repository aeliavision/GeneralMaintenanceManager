using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IExcelImportService
{
    public Task<ExcelImportResult> ImportAsync(
        string filePath,
        IProgress<ExcelImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
