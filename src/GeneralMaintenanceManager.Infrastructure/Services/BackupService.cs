using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Core.Enums;
using GeneralMaintenanceManager.Core.Models;
using GeneralMaintenanceManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class BackupService(
    DataPaths paths,
    IDataHealthService healthService,
    DatabaseInitializer databaseInitializer) : IBackupService, IDisposable
{
    private const int BackupFormatVersion = 2;
    private readonly RestoreSafetyPolicy _restoreSafetyPolicy = RestoreSafetyPolicy.FromEnvironment();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string BackupsDirectory => paths.BackupsDirectory;

    public string GetSuggestedBackupFileName()
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"MaintenanceManagerBackup_{timestamp}.zip";
    }

    public Task<BackupResult> CreateBackupAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateBackupRequestAsync(null, progress, cancellationToken);

    public Task<BackupResult> CreateBackupAsync(
        string destinationFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        return CreateBackupRequestAsync(destinationFilePath, progress, cancellationToken);
    }

    private async Task<BackupResult> CreateBackupRequestAsync(
        string? destinationFilePath,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, BackupProgressStage.Preparing, 0);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(progress, BackupProgressStage.ValidatingCurrentData, 4);
            await healthService.ValidateCurrentDataAsync(cancellationToken).ConfigureAwait(false);
            var result = await CreateBackupCoreAsync(
                "MaintenanceManagerBackup", progress, 8, 99, safetyBackup: false,
                cancellationToken, destinationFilePath).ConfigureAwait(false);
            Report(progress, BackupProgressStage.BackupCompleted, 100);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RestoreResult> RestoreBackupAsync(
        string backupFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        var sourcePath = Path.GetFullPath(backupFilePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Backup file was not found.", sourcePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workingRoot = Path.GetDirectoryName(paths.DataDirectory) ?? AppContext.BaseDirectory;
            var stagingRoot = Path.Combine(workingRoot, $".restore-staging-{Guid.NewGuid():N}");
            var stagedData = Path.Combine(stagingRoot, "Data");
            var previousData = Path.Combine(workingRoot, $".restore-quarantine-{Guid.NewGuid():N}");
            var safetyBackupPath = string.Empty;

            try
            {
                // Validate the incoming archive completely before touching current Data.
                // Current database corruption must never prevent restoration of a known-good backup.
                Directory.CreateDirectory(stagingRoot);
                Report(progress, BackupProgressStage.OpeningBackup, 4);
                await ExtractAndValidateAsync(sourcePath, stagingRoot, _restoreSafetyPolicy, progress, 6, 38, cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(stagedData))
                    throw new InvalidDataException("The selected file does not contain a Maintenance Manager Data folder.");

                ValidateStagedStructure(stagedData);
                await ValidateStagedDatabasesAsync(
                    stagedData, cancellationToken, progress, BackupProgressStage.ValidatingBackup, 40, 57).ConfigureAwait(false);

                // A conventional validated safety backup is desirable, but it is optional.
                // If the current DB is corrupt, failure here must not block the restore.
                if (Directory.Exists(paths.DataDirectory))
                {
                    Report(progress, BackupProgressStage.CreatingSafetyBackup, 59);
                    try
                    {
                        var safety = await CreateBackupCoreAsync(
                            "BeforeRestore", progress, 59, 68, safetyBackup: true,
                            cancellationToken, destinationFilePath: null).ConfigureAwait(false);
                        safetyBackupPath = safety.FilePath;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Preserve raw current Data in quarantine instead. The validated incoming
                        // backup remains eligible for activation even if current Data is damaged.
                    }
                }

                Report(progress, BackupProgressStage.ReplacingData, 70);
                if (Directory.Exists(paths.DataDirectory))
                    Directory.Move(paths.DataDirectory, previousData);

                try
                {
                    Directory.Move(stagedData, paths.DataDirectory);
                    Directory.CreateDirectory(paths.YearsDirectory);
                    Directory.CreateDirectory(paths.ImportLogsDirectory);
                    Directory.CreateDirectory(paths.SystemLogsDirectory);

                    Report(progress, BackupProgressStage.InitializingDatabase, 80);
                    await databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var year in paths.DiscoverExistingYears())
                        await databaseInitializer.EnsureYearAsync(year, cancellationToken).ConfigureAwait(false);

                    Report(progress, BackupProgressStage.VerifyingRestoredData, 91);
                    await healthService.ValidateCurrentDataAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    TryDeleteDirectory(paths.DataDirectory);
                    if (Directory.Exists(previousData))
                        Directory.Move(previousData, paths.DataDirectory);
                    throw;
                }

                // If a validated safety ZIP could not be created because old Data was damaged,
                // keep a raw quarantine archive/folder. Failure to package quarantine is not a
                // restore failure because the newly activated Data is already verified.
                if (Directory.Exists(previousData))
                {
                    if (safetyBackupPath.Length == 0)
                    {
                        safetyBackupPath = TryPackageQuarantine(previousData);
                        if (safetyBackupPath.Length == 0)
                            safetyBackupPath = previousData;
                    }

                    if (!string.Equals(safetyBackupPath, previousData, StringComparison.OrdinalIgnoreCase))
                        TryDeleteDirectory(previousData);
                }

                Report(progress, BackupProgressStage.RestoreCompleted, 100);
                return new RestoreResult(sourcePath, safetyBackupPath);
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupResult> CreateBackupCoreAsync(
        string prefix,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        bool safetyBackup,
        CancellationToken cancellationToken,
        string? destinationFilePath = null)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var now = DateTimeOffset.Now;
        var timestamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var finalPath = destinationFilePath is null ? GetUniqueBackupPath(prefix, timestamp) : NormalizeBackupDestination(destinationFilePath);
        var destinationDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The selected backup destination is invalid.");
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        var snapshotRoot = Path.Combine(paths.BackupsDirectory, $".snapshot-{Guid.NewGuid():N}");
        var snapshotData = Path.Combine(snapshotRoot, "Data");

        try
        {
            Directory.CreateDirectory(snapshotData);
            await CreateDataSnapshotAsync(snapshotData, progress, startPercent,
                MapPercent(startPercent, endPercent, 52), safetyBackup, cancellationToken).ConfigureAwait(false);
            ValidateStagedStructure(snapshotData);
            await ValidateStagedDatabasesAsync(snapshotData, cancellationToken, progress,
                safetyBackup ? BackupProgressStage.CreatingSafetyBackup : BackupProgressStage.ValidatingSnapshot,
                MapPercent(startPercent, endPercent, 54), MapPercent(startPercent, endPercent, 64)).ConfigureAwait(false);

            var snapshotFiles = Directory.EnumerateFiles(snapshotData, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
            var manifestFiles = new List<BackupManifestFile>(snapshotFiles.Length);
            foreach (var file in snapshotFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = "Data/" + Path.GetRelativePath(snapshotData, file).Replace(Path.DirectorySeparatorChar, '/');
                manifestFiles.Add(new BackupManifestFile(relative, new FileInfo(file).Length,
                    await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false)));
            }

            var manifest = new BackupManifest(
                BackupFormatVersion,
                now.ToUniversalTime(),
                typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                DatabaseInitializer.MasterFormatId,
                DatabaseInitializer.MasterSchemaVersion,
                DatabaseInitializer.AnnualFormatId,
                DatabaseInitializer.AnnualSchemaVersion,
                Environment.MachineName,
                manifestFiles);

            await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, useAsync: true))
            {
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestEntry = archive.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
                    await using (var manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    for (var index = 0; index < snapshotFiles.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ReportCore(progress, safetyBackup, BackupProgressStage.CompressingBackup,
                            Interpolate(MapPercent(startPercent, endPercent, 66), MapPercent(startPercent, endPercent, 94),
                                index + 1, Math.Max(1, snapshotFiles.Length)), index + 1, snapshotFiles.Length);
                        var relative = Path.GetRelativePath(snapshotData, snapshotFiles[index]).Replace(Path.DirectorySeparatorChar, '/');
                        var entry = archive.CreateEntry($"Data/{relative}", CompressionLevel.Optimal);
                        await using var input = new FileStream(snapshotFiles[index], FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
                        await using var output = entry.Open();
                        await input.CopyToAsync(output, 131072, cancellationToken).ConfigureAwait(false);
                    }
                }
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ReportCore(progress, safetyBackup, BackupProgressStage.FinalizingBackup, MapPercent(startPercent, endPercent, 97));
            File.Move(temporaryPath, finalPath, overwrite: true);
            return new BackupResult(finalPath, now, new FileInfo(finalPath).Length);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(snapshotRoot);
        }
    }

    private async Task CreateDataSnapshotAsync(
        string snapshotData,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        bool safetyBackup,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(paths.DataDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(snapshotData, Path.GetRelativePath(paths.DataDirectory, directory)));
        }

        var sourceFiles = Directory.EnumerateFiles(paths.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !IsTransientSqliteFile(file)).ToArray();
        for (var index = 0; index < sourceFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportCore(progress, safetyBackup, BackupProgressStage.SnapshottingData,
                Interpolate(startPercent, endPercent, index + 1, Math.Max(1, sourceFiles.Length)), index + 1, sourceFiles.Length);
            var file = sourceFiles[index];
            var destination = Path.Combine(snapshotData, Path.GetRelativePath(paths.DataDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (IsSqliteDatabase(file))
                await SnapshotSqliteDatabaseAsync(file, destination, cancellationToken).ConfigureAwait(false);
            else
                await CopyLiveFileAsync(file, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SnapshotSqliteDatabaseAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        SqliteException? lastBusyException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(destinationPath);
            try
            {
                var sourceBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
                    Pooling = false, DefaultTimeout = 10
                };
                var destinationBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private,
                    Pooling = false, DefaultTimeout = 10
                };
                await using var source = new SqliteConnection(sourceBuilder.ToString());
                await using var destination = new SqliteConnection(destinationBuilder.ToString());
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                lastBusyException = ex;
                TryDeleteFile(destinationPath);
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"Could not create a consistent SQLite snapshot for '{Path.GetFileName(sourcePath)}' because the database remained busy.", lastBusyException);
    }

    private static async Task CopyLiveFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
                await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, useAsync: true);
                await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                TryDeleteFile(destinationPath);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSqliteDatabase(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".db", StringComparison.OrdinalIgnoreCase);

    private string NormalizeBackupDestination(string destinationFilePath)
    {
        var fullPath = Path.GetFullPath(destinationFilePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase)) fullPath += ".zip";
        var dataRoot = Path.GetFullPath(paths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(fullPath);
        if (candidate.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a backup location outside the live Data folder.");
        return candidate;
    }

    private string GetUniqueBackupPath(string prefix, string timestamp)
    {
        var candidate = Path.Combine(paths.BackupsDirectory, $"{prefix}_{timestamp}.zip");
        if (!File.Exists(candidate)) return candidate;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            candidate = Path.Combine(paths.BackupsDirectory, $"{prefix}_{timestamp}_{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}.zip");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a unique backup file name.");
    }

    private string TryPackageQuarantine(string quarantineDirectory)
    {
        try
        {
            Directory.CreateDirectory(paths.BackupsDirectory);
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var destination = GetUniqueBackupPath("BeforeRestoreQuarantine", timestamp);
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                ZipFile.CreateFromDirectory(quarantineDirectory, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task ExtractAndValidateAsync(
        string sourcePath,
        string stagingRoot,
        RestoreSafetyPolicy safetyPolicy,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        var stagingDriveRoot = Path.GetPathRoot(Path.GetFullPath(stagingRoot));
        if (string.IsNullOrWhiteSpace(stagingDriveRoot))
            throw new IOException("Could not determine the restore staging drive.");
        var availableFreeSpace = new DriveInfo(stagingDriveRoot).AvailableFreeSpace;

        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName, "backup-manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1) throw new InvalidDataException("Backup manifest is missing or duplicated.");
        BackupManifest? manifest;
        await using (var stream = manifestEntries[0].Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (manifest is null || manifest.FormatVersion != BackupFormatVersion)
            throw new InvalidDataException("The selected backup format is not supported.");
        if (!string.Equals(manifest.MasterFormatId, DatabaseInitializer.MasterFormatId, StringComparison.Ordinal)
            || !string.Equals(manifest.AnnualFormatId, DatabaseInitializer.AnnualFormatId, StringComparison.Ordinal)
            || manifest.MasterSchemaVersion != DatabaseInitializer.MasterSchemaVersion
            || manifest.AnnualSchemaVersion != DatabaseInitializer.AnnualSchemaVersion)
            throw new InvalidDataException("The selected backup uses an incompatible database schema. Clean development schemas are not migrated during restore.");

        var manifestByPath = manifest.Files.ToDictionary(file => NormalizeArchivePath(file.Path), StringComparer.OrdinalIgnoreCase);
        var archiveFiles = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && !string.Equals(entry.FullName, "backup-manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifestByPath.Count != manifest.Files.Count || archiveFiles.Length != manifestByPath.Count)
            throw new InvalidDataException("The backup manifest/file list contains duplicate or inconsistent destinations.");

        long totalBytes = 0;
        foreach (var entry in archiveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = NormalizeArchivePath(entry.FullName);
            safetyPolicy.ValidateEntry(entry.Length, entry.CompressedLength, normalizedPath);
            totalBytes = checked(totalBytes + entry.Length);
        }
        safetyPolicy.ValidateArchive(archiveFiles.Length, totalBytes, availableFreeSpace);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[131072];
        for (var index = 0; index < archiveFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archiveFiles[index];
            var normalizedPath = NormalizeArchivePath(entry.FullName);
            if (!manifestByPath.TryGetValue(normalizedPath, out var manifestFile))
                throw new InvalidDataException("The backup contains a file not declared in its manifest.");
            if (manifestFile.SizeBytes != entry.Length)
                throw new InvalidDataException($"Backup file size does not match its manifest: {normalizedPath}");

            var destination = GetSafeRestoreDestination(stagingRoot, normalizedPath);
            if (!destinations.Add(destination)) throw new InvalidDataException("The backup contains duplicate destination paths.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, manifestFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup integrity hash mismatch: {normalizedPath}");

            Report(progress, BackupProgressStage.ExtractingBackup,
                Interpolate(startPercent, endPercent, index + 1, Math.Max(1, archiveFiles.Length)), index + 1, archiveFiles.Length);
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith("Data/", StringComparison.Ordinal)
            || normalized.Contains("../", StringComparison.Ordinal)
            || normalized.Contains("/..", StringComparison.Ordinal)
            || normalized.StartsWith('/')
            || normalized.Contains(':'))
            throw new InvalidDataException("The backup contains an unsafe file path.");
        return normalized;
    }

    private static string GetSafeRestoreDestination(string stagingRoot, string normalizedPath)
    {
        var destination = Path.GetFullPath(Path.Combine(stagingRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup contains an unsafe file path.");
        return destination;
    }

    private static void ValidateStagedStructure(string stagedData)
    {
        if (!File.Exists(Path.Combine(stagedData, "MaintenanceManager_Master.db")))
            throw new InvalidDataException("The backup does not contain the required MaintenanceManager_Master.db clean database.");
        var yearsDirectory = Path.Combine(stagedData, "Years");
        if (!Directory.Exists(yearsDirectory)) throw new InvalidDataException("The backup does not contain the required Data/Years directory.");

        var expectedMaster = Path.GetFullPath(Path.Combine(stagedData, "MaintenanceManager_Master.db"));
        var yearsRoot = Path.GetFullPath(yearsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var unexpectedDatabases = Directory.EnumerateFiles(stagedData, "*.db", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path =>
            {
                if (string.Equals(path, expectedMaster, StringComparison.OrdinalIgnoreCase)) return false;
                if (!path.StartsWith(yearsRoot, StringComparison.OrdinalIgnoreCase)) return true;

                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!fileName.StartsWith("Maintenance_", StringComparison.Ordinal)) return true;
                var yearText = fileName["Maintenance_".Length..];
                return !int.TryParse(yearText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
                    || year is < 1900 or > 9999;
            })
            .ToArray();
        if (unexpectedDatabases.Length > 0)
            throw new InvalidDataException("The backup contains an unexpected database file outside the canonical master/annual layout.");
    }

    private static async Task ValidateStagedDatabasesAsync(
        string stagedData,
        CancellationToken cancellationToken,
        IProgress<BackupProgress>? progress = null,
        BackupProgressStage stage = BackupProgressStage.ValidatingSnapshot,
        int startPercent = 0,
        int endPercent = 100)
    {
        var yearDatabases = Directory.Exists(Path.Combine(stagedData, "Years"))
            ? Directory.EnumerateFiles(Path.Combine(stagedData, "Years"), "Maintenance_*.db").ToArray()
            : Array.Empty<string>();
        var totalDatabases = 1 + yearDatabases.Length;
        var processed = 0;
        await DatabaseHealthService.ValidateDatabaseAsync(
            Path.Combine(stagedData, "MaintenanceManager_Master.db"),
            DatabaseInitializer.MasterFormatId, DatabaseInitializer.MasterSchemaVersion, null, cancellationToken).ConfigureAwait(false);
        Report(progress, stage, Interpolate(startPercent, endPercent, ++processed, totalDatabases), processed, totalDatabases);
        foreach (var yearDatabase in yearDatabases)
        {
            var fileName = Path.GetFileNameWithoutExtension(yearDatabase);
            var yearText = fileName["Maintenance_".Length..];
            if (!int.TryParse(yearText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year))
                throw new InvalidDataException($"Annual database file name is invalid: {Path.GetFileName(yearDatabase)}");
            await DatabaseHealthService.ValidateDatabaseAsync(yearDatabase, DatabaseInitializer.AnnualFormatId,
                DatabaseInitializer.AnnualSchemaVersion, year, cancellationToken).ConfigureAwait(false);
            Report(progress, stage, Interpolate(startPercent, endPercent, ++processed, totalDatabases), processed, totalDatabases);
        }
    }

    private static bool IsTransientSqliteFile(string filePath) =>
        filePath.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try { File.Delete(filePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Report(IProgress<BackupProgress>? progress, BackupProgressStage stage, int percent, int processedItems = 0, int totalItems = 0) =>
        progress?.Report(new BackupProgress(stage, Math.Clamp(percent, 0, 100), processedItems, totalItems));

    private static void ReportCore(IProgress<BackupProgress>? progress, bool safetyBackup, BackupProgressStage stage, int percent, int processedItems = 0, int totalItems = 0) =>
        Report(progress, safetyBackup ? BackupProgressStage.CreatingSafetyBackup : stage, percent, processedItems, totalItems);

    private static int MapPercent(int startPercent, int endPercent, int localPercent) =>
        startPercent + (int)Math.Round((endPercent - startPercent) * (Math.Clamp(localPercent, 0, 100) / 100d), MidpointRounding.AwayFromZero);

    private static int Interpolate(int startPercent, int endPercent, int processedItems, int totalItems)
    {
        if (totalItems <= 0) return endPercent;
        var fraction = Math.Clamp(processedItems / (double)totalItems, 0d, 1d);
        return startPercent + (int)Math.Round((endPercent - startPercent) * fraction, MidpointRounding.AwayFromZero);
    }

    private sealed record BackupManifest(
        int FormatVersion,
        DateTimeOffset CreatedAtUtc,
        string AppVersion,
        string MasterFormatId,
        int MasterSchemaVersion,
        string AnnualFormatId,
        int AnnualSchemaVersion,
        string SourceMachine,
        IReadOnlyList<BackupManifestFile> Files);

    private sealed record BackupManifestFile(string Path, long SizeBytes, string Sha256);

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
