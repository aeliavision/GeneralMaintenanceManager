namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IAssetExportService
{
    public Task<int> ExportExcelAsync(string filePath, CancellationToken cancellationToken = default);

    public Task<int> ExportCsvAsync(string filePath, CancellationToken cancellationToken = default);
}
