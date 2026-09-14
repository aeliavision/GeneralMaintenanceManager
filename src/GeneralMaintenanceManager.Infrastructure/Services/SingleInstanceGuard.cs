using System.Security.Cryptography;
using System.Text;
using GeneralMaintenanceManager.Infrastructure.Data;

namespace GeneralMaintenanceManager.Infrastructure.Services;

public sealed class SingleInstanceGuard(DataPaths paths) : IDisposable
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutex is not null) return _ownsMutex;

        var normalized = Path.GetFullPath(paths.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutex = new Mutex(initiallyOwned: true, $"GMM.Data.{hash}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        _ownsMutex = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _ownsMutex = false;
        _mutex?.Dispose();
        _mutex = null;
        GC.SuppressFinalize(this);
    }
}
