namespace AorinEQ.Core;

/// <summary>ONE loopback capture and ONE analysis for the whole application.
///
/// WHY IT IS REFERENCE COUNTED. Before v3.5.0 the EQ editor owned a <see cref="LoopbackCapture"/>
/// outright and started it when it opened. With persistent widgets there are now several
/// independent surfaces that each want the same stream, appearing and disappearing in any order,
/// and "one capture per surface" would mean a WASAPI client, a capture thread, an event handle and
/// a COM object per widget — for a signal that is byte-for-byte the same in all of them.
///
/// So the capture belongs to nobody in particular. Each surface takes a registration while it is
/// visible and needs audio; the capture starts on the FIRST and stops on the LAST. A registration
/// is an <see cref="IDisposable"/> and releasing it twice counts once, because a widget torn down
/// mid-frame can reach its own teardown by two paths, and a double decrement would stop the
/// capture out from under every other widget still watching.
///
/// The counter is the only thing under the lock; nothing here calls into COM while holding it
/// beyond the start/stop the transition itself requires.</summary>
public sealed class SharedAudioPipeline : IDisposable
{
    private readonly object _lock = new();
    private readonly LoopbackCapture _capture = new();
    private readonly AudioAnalyzer _analyzer = new();
    private readonly Dictionary<long, string> _consumers = new();
    private long _nextToken;
    private bool _disposed;

    public SharedAudioPipeline()
    {
        _capture.SamplesAvailable += _analyzer.Feed;
    }

    /// <summary>How many surfaces currently hold a registration.</summary>
    public int ConsumerCount
    {
        get { lock (_lock) return _consumers.Count; }
    }

    /// <summary>Who they are. Diagnostic: a capture that will not stop can be pointed at its
    /// owner instead of being guessed at.</summary>
    public IReadOnlyList<string> ConsumerNames
    {
        get { lock (_lock) return _consumers.Values.ToList(); }
    }

    public bool IsCapturing { get; private set; }

    /// <summary>The capture's mix-format sample rate, 0 while stopped.</summary>
    public int SampleRate => _capture.SampleRate;

    /// <summary>Takes a registration for as long as <paramref name="name"/> needs the stream.
    /// Dispose it to release. Disposing twice is safe and counts once; a registration taken from
    /// an already-disposed pipeline is inert rather than an exception, because widgets can and do
    /// close after the pipeline has gone.</summary>
    public IDisposable AddConsumer(string name)
    {
        lock (_lock)
        {
            if (_disposed) return InertRegistration.Instance;
            long token = _nextToken++;
            _consumers[token] = name;
            if (_consumers.Count == 1)
                StartLocked();
            return new Registration(this, token);
        }
    }

    private void Release(long token)
    {
        lock (_lock)
        {
            if (!_consumers.Remove(token)) return; // already released, or the pipeline is gone
            if (_consumers.Count == 0)
                StopLocked();
        }
    }

    private void StartLocked()
    {
        // A capture that fails to attach (no endpoint, audio service down) is not an error the
        // caller can do anything about: the widgets simply show silence, and the next transition
        // — or a Restart on a device change — tries again.
        IsCapturing = _capture.Start();
        _analyzer.SampleRate = _capture.SampleRate;
        _analyzer.Reset();
    }

    private void StopLocked()
    {
        _capture.Stop();
        IsCapturing = false;
        _analyzer.SampleRate = 0;
        _analyzer.Reset();
    }

    /// <summary>Re-attaches to the CURRENT default render endpoint — the default-device-change
    /// path. A no-op when nobody is watching: restarting then would start a capture nobody asked
    /// for, and the next consumer attaches to the right device anyway.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            if (_disposed || _consumers.Count == 0) return;
            _capture.Stop();
            StartLocked();
        }
    }

    /// <summary>The current reading, computed at most once per arrival of new samples however
    /// many surfaces ask. See <see cref="AudioAnalyzer"/>.</summary>
    public AudioAnalysis Analyze() => _analyzer.Analyze();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _consumers.Clear();
            StopLocked();
        }
        _capture.SamplesAvailable -= _analyzer.Feed;
        _capture.Dispose();
    }

    /// <summary>One surface's hold on the capture.
    ///
    /// A double release is guarded TWICE, independently, and deliberately so: the Interlocked
    /// exchange here means a repeated Dispose never reaches the pipeline at all, and the token
    /// bookkeeping in <see cref="Release"/> means that even if it did, removing a token that is
    /// already gone cannot bring the count down a second time. Tokens are never reused, so the
    /// second guard cannot be defeated by a stale handle either.
    ///
    /// Measured, not assumed: breaking either guard alone leaves the behaviour correct, and it
    /// takes removing BOTH — the plain reference count a fresh implementation would reach for — to
    /// make the capture stop while another widget is still drawing from it. That is what the
    /// control run for this behaviour does.</summary>
    private sealed class Registration : IDisposable
    {
        private SharedAudioPipeline? _owner;
        private readonly long _token;

        public Registration(SharedAudioPipeline owner, long token)
        {
            _owner = owner;
            _token = token;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_token);
    }

    /// <summary>What a disposed pipeline hands back: a registration that owns nothing.</summary>
    private sealed class InertRegistration : IDisposable
    {
        public static readonly InertRegistration Instance = new();
        public void Dispose() { }
    }
}
