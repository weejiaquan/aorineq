namespace ApoVolume.Core;

/// <summary>
/// Runs the first posted action near-immediately on a ThreadPool thread (never synchronously
/// on the caller), then at most one action per interval, always executing the most recently
/// posted action (trailing edge).
/// </summary>
public sealed class Coalescer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly object _runLock = new();
    private Action? _pending;
    private bool _cooldown;
    private bool _disposed;

    public Coalescer(TimeSpan interval)
    {
        _interval = interval;
        _timer = new Timer(_ => OnTimer());
    }

    public void Post(Action action)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _pending = action; // latest-wins: overwrites whatever hasn't run yet
            if (_cooldown)
            {
                return; // already scheduled; will be picked up by the current cooldown cycle
            }
            _cooldown = true;
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); // fire promptly, off the caller's thread
        }
    }

    private void OnTimer()
    {
        Action? run;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            run = _pending;
            _pending = null;
            if (run is null)
            {
                _cooldown = false; // idle: nothing arrived during the last interval
                return;
            }
        }
        lock (_runLock)
        {
            try
            {
                run();
            }
            catch (Exception)
            {
                // Actions own their error handling. This guard only prevents an unhandled
                // exception on the ThreadPool timer thread from killing the process.
            }
        }
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            // Re-arm for the cooldown window: catches anything posted while `run` executed.
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
        _timer.Dispose();
    }
}
