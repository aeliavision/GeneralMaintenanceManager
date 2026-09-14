namespace GeneralMaintenanceManager.Infrastructure.Services;

/// <summary>
/// Coordinates ordinary database work with destructive operations such as restore.
/// Shared/read leases may run concurrently; an exclusive lease waits until every open
/// database context/read operation has left. Exclusive ownership is re-entrant for the
/// current async flow so restore/maintenance code can create DbContexts safely while
/// the outer exclusive lease is held.
/// </summary>
public sealed class DatabaseActivityGate : IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _readerMutex = new(1, 1);
    private readonly SemaphoreSlim _roomEmpty = new(1, 1);
    private readonly AsyncLocal<ExclusiveContext?> _exclusiveContext = new();
    private int _readerCount;
    private bool _disposed;

    public IDisposable EnterRead()
    {
        ThrowIfDisposed();
        if (_exclusiveContext.Value?.Depth > 0) return NoopLease.Instance;

        _turnstile.Wait();
        _turnstile.Release();

        _readerMutex.Wait();
        try
        {
            if (_readerCount == 0) _roomEmpty.Wait();
            _readerCount++;
        }
        finally { _readerMutex.Release(); }

        return new Lease(ReleaseReader);
    }

    public async Task<IDisposable> EnterReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_exclusiveContext.Value?.Depth > 0) return NoopLease.Instance;

        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turnstile.Release();

        await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_readerCount == 0) await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
            _readerCount++;
        }
        finally { _readerMutex.Release(); }

        return new Lease(ReleaseReader);
    }

    public Task<IDisposable> EnterExclusiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var context = _exclusiveContext.Value;
        if (context?.Depth > 0)
        {
            context.Depth++;
            return Task.FromResult<IDisposable>(new Lease(() => context.Depth--));
        }

        // Set the holder synchronously in the caller's ExecutionContext. The async
        // acquisition mutates the shared holder after the writer lock is acquired;
        // this makes exclusive ownership visible to DbContext creation after await.
        context ??= new ExclusiveContext();
        _exclusiveContext.Value = context;
        return EnterExclusiveCoreAsync(context, cancellationToken);
    }

    private async Task<IDisposable> EnterExclusiveCoreAsync(ExclusiveContext context, CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            _turnstile.Release();
            throw;
        }

        context.Depth = 1;
        return new Lease(() =>
        {
            context.Depth = 0;
            _roomEmpty.Release();
            _turnstile.Release();
        });
    }

    private void ReleaseReader()
    {
        _readerMutex.Wait();
        try
        {
            if (_readerCount <= 0) throw new InvalidOperationException("Database reader lease count is inconsistent.");
            _readerCount--;
            if (_readerCount == 0) _roomEmpty.Release();
        }
        finally { _readerMutex.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _turnstile.Dispose();
        _readerMutex.Dispose();
        _roomEmpty.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class ExclusiveContext
    {
        public int Depth { get; set; }
    }
}
