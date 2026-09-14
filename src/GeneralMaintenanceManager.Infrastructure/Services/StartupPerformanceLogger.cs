using System.Diagnostics;
using System.Globalization;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class StartupPerformanceLogger(DataPaths paths)
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private long _previousStageMs;

    public void LogStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        var totalMs = _timer.ElapsedMilliseconds;
        var stageMs = totalMs - Interlocked.Exchange(ref _previousStageMs, totalMs);
        try
        {
            Directory.CreateDirectory(paths.SystemLogsDirectory);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} | {stage} | stage={stageMs}ms | total={totalMs}ms{Environment.NewLine}");
            File.AppendAllText(Path.Combine(paths.SystemLogsDirectory, "startup-performance.log"), line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup diagnostics are best-effort only.
        }
    }
}
