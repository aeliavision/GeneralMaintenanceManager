using System.IO;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Tests;

internal sealed class TestDataScope : IDisposable
{
    private readonly string _directory;

    public TestDataScope()
    {
        _directory = Path.Combine(Path.GetTempPath(), "GeneralMaintenanceManager.Tests", Guid.NewGuid().ToString("N"));
        Paths = new DataPaths(_directory);
    }

    public DataPaths Paths { get; }

    public void Dispose()
    {
        DeleteDirectory(Paths.BackupsDirectory);
        DeleteDirectory(_directory);
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
