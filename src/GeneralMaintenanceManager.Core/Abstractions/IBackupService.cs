using GeneralMaintenanceManager.Core.Models;

namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IBackupService
{
    public string BackupsDirectory { get; }

    public string GetSuggestedBackupFileName();

    public Task<BackupResult> CreateBackupAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public Task<BackupResult> CreateBackupAsync(
        string destinationFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public Task<RestoreResult> RestoreBackupAsync(
        string backupFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
