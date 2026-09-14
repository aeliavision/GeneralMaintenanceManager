using System.Globalization;

namespace GeneralMaintenanceManager.Infrastructure.Services;

/// <summary>
/// Capacity-aware safety limits for restoring potentially multi-gigabyte database backups.
/// Values can be overridden for controlled deployments with GMM_RESTORE_* environment variables.
/// </summary>
internal sealed class RestoreSafetyPolicy
{
    private const long GiB = 1024L * 1024L * 1024L;

    public long MaxTotalUncompressedBytes { get; init; } = 256L * GiB;
    public long MaxSingleEntryBytes { get; init; } = 128L * GiB;
    public int MaxEntries { get; init; } = 100_000;
    public double MaxExpansionRatio { get; init; } = 500d;
    public long FreeSpaceReserveBytes { get; init; } = 2L * GiB;

    public static RestoreSafetyPolicy FromEnvironment() => new()
    {
        MaxTotalUncompressedBytes = ReadGiB("GMM_RESTORE_MAX_TOTAL_GB", 256),
        MaxSingleEntryBytes = ReadGiB("GMM_RESTORE_MAX_ENTRY_GB", 128),
        MaxEntries = ReadInt("GMM_RESTORE_MAX_ENTRIES", 100_000),
        MaxExpansionRatio = ReadDouble("GMM_RESTORE_MAX_EXPANSION_RATIO", 500d),
        FreeSpaceReserveBytes = ReadGiB("GMM_RESTORE_FREE_SPACE_RESERVE_GB", 2)
    };

    public void ValidateEntry(long uncompressedBytes, long compressedBytes, string path)
    {
        if (uncompressedBytes < 0 || compressedBytes < 0)
            throw new InvalidDataException($"Backup entry '{path}' has invalid size metadata.");
        if (uncompressedBytes > MaxSingleEntryBytes)
            throw new InvalidDataException($"Backup entry '{path}' exceeds the configured restore file-size limit.");
        if (uncompressedBytes == 0) return;
        if (compressedBytes == 0)
            throw new InvalidDataException($"Backup entry '{path}' has an unsafe compression ratio.");
        var ratio = (double)uncompressedBytes / compressedBytes;
        if (ratio > MaxExpansionRatio)
            throw new InvalidDataException($"Backup entry '{path}' exceeds the configured compression expansion ratio.");
    }

    public void ValidateArchive(int entryCount, long totalUncompressedBytes, long availableFreeSpace)
    {
        if (entryCount <= 0 || entryCount > MaxEntries)
            throw new InvalidDataException("The selected backup has an invalid number of entries.");
        if (totalUncompressedBytes < 0 || totalUncompressedBytes > MaxTotalUncompressedBytes)
            throw new InvalidDataException("The selected backup exceeds the configured total restore-size limit.");
        if (availableFreeSpace < totalUncompressedBytes + FreeSpaceReserveBytes)
            throw new IOException("There is not enough free disk space to stage and safely validate this backup.");
    }

    private static long ReadGiB(string name, long fallbackGiB)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gib) || gib <= 0)
            gib = fallbackGiB;
        return checked(gib * GiB);
    }

    private static int ReadInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static double ReadDouble(string name, double fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 1d
            ? parsed
            : fallback;
    }
}
