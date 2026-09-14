using System.Globalization;
using System.Text;
using GeneralMaintenanceManager.Core.Abstractions;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class DiagnosticLogger(DataPaths paths) : IDiagnosticLogger
{
    private const long MaxLogBytes = 1_048_576;
    private const int MaxLogFiles = 10;
    private readonly object _sync = new();

    public void Info(string eventName, string message) => Write("INFO", eventName, message, null);

    public void LogError(string eventName, Exception exception, string message) =>
        Write("ERROR", eventName, message, exception);

    private void Write(string level, string eventName, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(paths.SystemLogsDirectory);
                var path = ResolveCurrentPath();
                var line = new StringBuilder()
                    .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(level).Append(' ')
                    .Append('[').Append(Clean(eventName)).Append("] ")
                    .AppendLine(Clean(message));
                if (exception is not null) line.AppendLine(exception.ToString());
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
                TrimOldLogs();
            }
        }
        catch
        {
            // Diagnostics must never hide or replace the original application outcome.
        }
    }

    private string ResolveCurrentPath()
    {
        var stem = $"gmm-{DateTime.UtcNow:yyyyMMdd}";
        var primary = Path.Combine(paths.SystemLogsDirectory, stem + ".log");
        if (!File.Exists(primary) || new FileInfo(primary).Length < MaxLogBytes) return primary;
        for (var index = 1; index < 100; index++)
        {
            var candidate = Path.Combine(paths.SystemLogsDirectory, $"{stem}-{index:00}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxLogBytes) return candidate;
        }
        return Path.Combine(paths.SystemLogsDirectory, $"{stem}-99.log");
    }

    private void TrimOldLogs()
    {
        var files = new DirectoryInfo(paths.SystemLogsDirectory)
            .GetFiles("gmm-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (var file in files.Skip(MaxLogFiles))
        {
            try { file.Delete(); } catch { }
        }
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
}
