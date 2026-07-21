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
    private Action? _pending;
    private bool _cooldown;

    public Coalescer(TimeSpan interval)
    {
        _interval = interval;
        _timer = new Timer(_ => OnTimer());
    }

    public void Post(Action action)
    {
        lock (_lock)
        {
            if (_cooldown)
            {
                _pending = action;
                return;
            }
            _cooldown = true;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
        action();
    }

    private void OnTimer()
    {
        Action? run;
        lock (_lock)
        {
            run = _pending;
            _pending = null;
            if (run is null)
            {
                _cooldown = false;
                return;
            }
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
        run();
    }

    public void Dispose() => _timer.Dispose();
}
