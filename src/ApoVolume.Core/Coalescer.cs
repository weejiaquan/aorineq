namespace ApoVolume.Core;

/// <summary>
/// Runs the first action immediately (leading edge), then at most one action per interval,
/// always executing the most recently posted action (trailing edge).
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
            if (_cooldown)
            {
                _pending = action;
                return;
            }
            _cooldown = true;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
        // Leading-edge execution: exceptions propagate to the caller, which has context.
        lock (_runLock)
        {
            action();
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
                _cooldown = false;
                return;
            }
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
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
