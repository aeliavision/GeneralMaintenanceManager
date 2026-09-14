namespace GeneralMaintenanceManager.Core.Models;

public sealed record BackupResult(
    string FilePath,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes);
