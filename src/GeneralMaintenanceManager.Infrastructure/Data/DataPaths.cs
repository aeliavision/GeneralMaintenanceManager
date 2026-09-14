using System.Globalization;
using System.IO;
using GeneralMaintenanceManager.Core.Abstractions;

namespace GeneralMaintenanceManager.Infrastructure.Data;

public sealed class DataPaths : IDataLocationService
{
    private const string AnnualPrefix = "Maintenance_";

    public DataPaths() : this(DefaultDataDirectory, useApplicationBackupDirectory: true) { }
    public DataPaths(string dataDirectory) : this(dataDirectory, useApplicationBackupDirectory: false) { }

    private DataPaths(string dataDirectory, bool useApplicationBackupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        YearsDirectory = Path.Combine(DataDirectory, "Years");
        ImportLogsDirectory = Path.Combine(DataDirectory, "ImportLogs");
        SystemLogsDirectory = Path.Combine(DataDirectory, "SystemLogs");
        BackupsDirectory = useApplicationBackupDirectory
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Backups"))
            : Path.GetFullPath(dataDirectory + ".Backups");
        MasterDatabasePath = Path.Combine(DataDirectory, "MaintenanceManager_Master.db");

        Directory.CreateDirectory(DataDirectory);
        EnsureWritableDirectory(DataDirectory);
        Directory.CreateDirectory(YearsDirectory);
        Directory.CreateDirectory(ImportLogsDirectory);
        Directory.CreateDirectory(SystemLogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }

    public static string DefaultDataDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data"));
    public string DataDirectory { get; }
    public string MasterDatabasePath { get; }
    public string YearsDirectory { get; }
    public string ImportLogsDirectory { get; }
    public string SystemLogsDirectory { get; }
    public string BackupsDirectory { get; }

    public string GetYearDatabasePath(int year)
    {
        ValidateYear(year);
        return Path.Combine(YearsDirectory, $"{AnnualPrefix}{year.ToString("0000", CultureInfo.InvariantCulture)}.db");
    }

    public IReadOnlyList<int> DiscoverExistingYears()
    {
        if (!Directory.Exists(YearsDirectory)) return [];

        var years = new SortedSet<int>(Comparer<int>.Create((left, right) => right.CompareTo(left)));
        foreach (var filePath in Directory.EnumerateFiles(YearsDirectory, $"{AnnualPrefix}*.db"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!fileName.StartsWith(AnnualPrefix, StringComparison.Ordinal)) continue;
            var yearText = fileName[AnnualPrefix.Length..];
            if (int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= 1900 and <= 9999)
            {
                years.Add(year);
            }
        }

        return [.. years];
    }

    public static void ValidateYear(int year)
    {
        if (year is < 1900 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
    }

    private static void EnsureWritableDirectory(string directory)
    {
        var probePath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The application data folder '{directory}' is not writable. General Maintenance Manager stores its databases beside the application. Move the application folder to a writable location and start it again.", ex);
        }
        finally
        {
            if (File.Exists(probePath)) File.Delete(probePath);
        }
    }
}
