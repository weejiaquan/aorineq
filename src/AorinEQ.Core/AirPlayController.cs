using AorinEQ.Core.Raop;

namespace AorinEQ.Core;

/// <summary>Owns one AirPlay stream: a loopback capture of the chosen endpoint feeding a
/// <see cref="RaopSession"/>.
///
/// It keeps its OWN <see cref="LoopbackCapture"/> rather than joining
/// <see cref="SharedAudioPipeline"/>, deliberately. The pipeline exists to share one capture of
/// the DEFAULT endpoint among visualizers that appear and disappear, and to stop it the moment
/// the last one closes. A stream needs a capture of a CHOSEN endpoint that runs continuously.
/// Teaching one class both selection policies would complicate its reference counting for no
/// gain; one extra WASAPI client is the cheaper trade, and the visualizers keep their
/// zero-idle-cost property untouched.</summary>
public sealed class AirPlayController : IDisposable
{
    private readonly object _gate = new();
    private LoopbackCapture? _capture;
    private RaopSession? _session;
    private AirPlayDevice? _current;
    private short[] _scratch = [];
    private bool _disposed;
    private int _volumePercent = AirPlaySetting.Default.VolumePercent;

    /// <summary>Raised when the stream starts, stops or fails. Fires on a background thread —
    /// the UI must marshal, the same contract as EndpointVolume.Changed.</summary>
    public event Action? StateChanged;

    public bool IsStreaming
    {
        get { lock (_gate) return _session?.IsStreaming == true; }
    }

    public AirPlayDevice? Current
    {
        get { lock (_gate) return _current; }
    }

    public int VolumePercent
    {
        get { lock (_gate) return _volumePercent; }
    }

    /// <summary>Whether the volume keys should drive the RECEIVER rather than Windows.
    ///
    /// False in Equalizer APO mode however the setting is left, and that is not a limitation
    /// but the point: the preamp sits upstream of the loopback tap, so the keys have ALREADY
    /// attenuated the samples by the time they are packetised. Retargeting as well would apply
    /// the same attenuation twice — once digitally and once at the receiver.
    ///
    /// True in system mode while streaming, because endpoint volume reaches the tap only on
    /// devices without hardware volume, and Microsoft documents that behaviour as not
    /// contractual. Neither outcome can be relied on, so the sender sets the receiver's volume
    /// explicitly instead.</summary>
    public static bool ShouldRetargetVolume(string volumeMode, bool autoRetarget, bool streaming) =>
        streaming && autoRetarget && volumeMode == VolumeModes.System;

    /// <summary>Whether the AutoRetargetVolume setting means anything in this volume mode. The
    /// Settings page greys the checkbox and explains itself when this is false.</summary>
    public static bool RetargetSettingApplies(string volumeMode) => volumeMode == VolumeModes.System;

    /// <summary>Starts streaming <paramref name="sourceEndpointId"/> (empty = the default render
    /// endpoint) to <paramref name="device"/>. False when the capture or the handshake fails;
    /// <see cref="Snapshot"/> then carries the reason.</summary>
    public bool Start(AirPlayDevice device, string? sourceEndpointId, string mode, int customQueueMs)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            StopLocked();

            int queueMs = AirPlayModes.QueueMs(mode, customQueueMs);
            var session = new RaopSession(device, queueMs);
            session.Failed += OnSessionFailed;

            if (!session.Connect())
            {
                _session = session;   // retained so Snapshot() can report why it failed
                _current = device;
                StateChanged?.Invoke();
                return false;
            }

            var capture = new LoopbackCapture();
            capture.SamplesAvailable += OnSamples;
            // Ask the engine for 44100 so no resampler is needed. If a driver refuses, the rate
            // comes back different and the samples are pushed anyway - pitch-shifted, audibly
            // wrong, but diagnosable from the rate shown on the Settings page rather than
            // silently mysterious.
            if (!capture.Start(string.IsNullOrEmpty(sourceEndpointId) ? null : sourceEndpointId,
                    RaopSession.SampleRate))
            {
                capture.SamplesAvailable -= OnSamples;
                capture.Dispose();
                session.Disconnect();
                session.Failed -= OnSessionFailed;
                _session = null;
                _current = null;
                StateChanged?.Invoke();
                return false;
            }

            _capture = capture;
            _session = session;
            _current = device;
            session.SetVolume(AirPlayVolume.ToDb(_volumePercent));
        }
        StateChanged?.Invoke();
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_session is null && _capture is null) return;
            StopLocked();
        }
        StateChanged?.Invoke();
    }

    private void StopLocked()
    {
        if (_capture is not null)
        {
            _capture.SamplesAvailable -= OnSamples;
            _capture.Dispose();
            _capture = null;
        }
        if (_session is not null)
        {
            _session.Failed -= OnSessionFailed;
            _session.Disconnect();
            _session.Dispose();
            _session = null;
        }
        _current = null;
    }

    /// <summary>Sets the receiver's volume. Remembered while idle so the next session starts at
    /// the level the user last chose rather than a default.</summary>
    public void SetVolumePercent(int percent)
    {
        RaopSession? session;
        double db;
        lock (_gate)
        {
            _volumePercent = Math.Clamp(percent, 0, 100);
            db = AirPlayVolume.ToDb(_volumePercent);
            session = _session;
        }
        session?.SetVolume(db);
    }

    public RaopDiagnostics Snapshot()
    {
        lock (_gate) return _session?.Snapshot() ?? RaopDiagnostics.Idle;
    }

    /// <summary>Converts a capture block to interleaved 16-bit and hands it to the session.
    ///
    /// Runs on the capture thread. The scratch buffer is reused across callbacks and only ever
    /// touched here — a fresh array every 10 ms would be pure GC pressure in a tray app that
    /// may stream for hours.</summary>
    private void OnSamples(float[] left, float[] right)
    {
        var session = _session;
        if (session is null || !session.IsStreaming) return;

        int frames = Math.Min(left.Length, right.Length);
        if (_scratch.Length < frames * 2)
            _scratch = new short[frames * 2];

        for (int i = 0; i < frames; i++)
        {
            _scratch[i * 2] = ToPcm(left[i]);
            _scratch[i * 2 + 1] = ToPcm(right[i]);
        }
        session.PushSamples(_scratch.AsSpan(0, frames * 2), frames);
    }

    /// <summary>Float to 16-bit with clamping. The loopback stream is post-APO and an EQ with
    /// boost can legitimately exceed full scale, so this must saturate rather than wrap — a
    /// wrapped sample is a loud click.</summary>
    private static short ToPcm(float sample) =>
        (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

    private void OnSessionFailed(string reason)
    {
        lock (_gate)
        {
            // Keep the session object so its diagnostics still name the failure; only the
            // capture is released, because it is the expensive half.
            if (_capture is not null)
            {
                _capture.SamplesAvailable -= OnSamples;
                _capture.Dispose();
                _capture = null;
            }
        }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}
