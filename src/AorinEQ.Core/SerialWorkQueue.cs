namespace AorinEQ.Core;

/// <summary>One background thread that runs posted actions in the order they were posted, and a
/// <see cref="Post"/> that never runs anything on the caller's thread and never waits for the
/// worker.
///
/// WHAT IT IS FOR. A COM notification callback (IMMNotificationClient, IAudioEndpointVolumeCallback)
/// arrives on a thread the notifying subsystem owns, and MMDevAPI in particular does not permit a
/// blocking call back into it from inside one — the call does not return, the dispatch thread stays
/// stuck there for the life of the process, and nothing is ever delivered again. So a callback may
/// only capture what it needs and hand it here.
///
/// THE GUARANTEES, because a queue that silently reorders or drops would trade a loud bug for a
/// quiet one:
///   * FIFO. Actions run in post order, one at a time, never concurrently with each other.
///   * Nothing is dropped before <see cref="Dispose"/> — every posted action runs.
///   * Nothing runs after <see cref="Dispose"/> returns. Post is refused (returns false) and work
///     that was queued but had not started is discarded rather than run against torn-down state.
///   * <see cref="Post"/> is non-blocking regardless of what the worker is doing.
///   * An action that throws does not kill the worker or the process; the next action still runs.
///
/// <see cref="Dispose"/> waits only a bounded time for an action that is already running: the
/// in-flight call may be inside COM, where it cannot be cancelled, and shutdown must not hang on
/// it. The thread is a background thread, so a stuck one cannot keep the process alive either.</summary>
public sealed class SerialWorkQueue : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> gives an already-running action to finish before it
    /// stops waiting. Long enough for a normal COM round trip, short enough not to be felt at exit.</summary>
    public static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Queue<Action> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    /// <param name="threadName">Names the worker thread, so a stack captured from a hung process
    /// says which queue it belongs to.</param>
    public SerialWorkQueue(string threadName)
    {
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
        };
        _worker.Start();
    }

    /// <summary>Queues <paramref name="work"/> to run on the worker thread. Returns immediately,
    /// and returns false when the queue is disposed (in which case the action never runs).</summary>
    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }
            _queue.Enqueue(work);
            Monitor.Pulse(_lock);
            return true;
        }
    }

    private void Run()
    {
        while (true)
        {
            Action work;
            lock (_lock)
            {
                while (!_disposed && _queue.Count == 0)
                {
                    Monitor.Wait(_lock);
                }
                if (_disposed)
                {
                    return; // discarded on purpose: see the contract above
                }
                work = _queue.Dequeue();
            }
            try
            {
                work();
            }
            catch (Exception)
            {
                // Actions own their error handling. This guard exists so one bad action cannot
                // take the worker (and with it every later notification) down with it.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _queue.Clear();
            Monitor.PulseAll(_lock);
        }
        // Disposing from inside a posted action would otherwise join the worker to itself.
        if (Thread.CurrentThread != _worker)
        {
            _worker.Join(DisposeJoinTimeout);
        }
    }
}
