using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Windows master volume/mute on the default render endpoint, for "system" volume mode.
/// Percent maps 1:1 to the Windows slider scale (scalar × 100 — no taper math). Every set passes
/// this instance's own event-context GUID, and notifications carrying that context are swallowed,
/// so <see cref="Changed"/> only fires for EXTERNAL changes (another app, the Windows mixer, a
/// device switch). <see cref="Changed"/> is raised on a background thread — UI callers must
/// marshal (same contract as KeyboardHook's events).
///
/// THE COM CALLBACKS ONLY CAPTURE AND HAND OFF (v3.4.1). MMDevAPI delivers its notifications on a
/// thread it owns, and a blocking call back into MMDevAPI from inside one does not return: the
/// dispatch thread stays stuck there for the life of the process and NOTHING IS EVER DELIVERED
/// AGAIN. Until v3.4.1 this class did its re-activation work (unregister, release, activate,
/// register, read) inline in <see cref="NotificationClient.OnDefaultDeviceChanged"/>, so the very
/// first default-device change deadlocked that thread — the app saw exactly one device change per
/// session and then silently stopped following the user's output, and Dispose hung behind the same
/// lock. Both callbacks now do the minimum the callback thread is allowed to do (read a struct that
/// is only valid for the duration of the call, compare a GUID) and post the rest to
/// <see cref="_notifications"/>. Nothing in this class may call MMDevAPI from a callback thread.
///
/// Error contract: never throws to callers. Device loss / audio service down make methods return
/// false/null, and the next call lazily retries activation (a default-device change also forces
/// re-activation onto the new endpoint). Dispose unregisters both callbacks and releases COM.</summary>
public sealed class EndpointVolume : IDisposable
{
    private const int ClsCtxAll = 0x17; // CLSCTX_ALL

    /// <summary>How long <see cref="Dispose"/> waits for the instance lock before giving up on the
    /// COM teardown. Only reachable if a notification action is wedged inside a COM call that
    /// cannot be cancelled; the process is on its way out and must not hang for it.</summary>
    private static readonly TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Guid _eventContext = Guid.NewGuid();
    private readonly VolumeCallback _volumeCallback;
    private readonly NotificationClient _notificationClient;
    /// <summary>Everything the COM callbacks would otherwise have done inline, run in arrival
    /// order on one thread that is ours, not MMDevAPI's.</summary>
    private readonly SerialWorkQueue _notifications = new("AorinEQ endpoint notifications");
    private AudioEndpoint.IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    /// <summary>Endpoint id the currently activated <see cref="_volume"/> belongs to — stamped
    /// onto every <see cref="Changed"/> so consumers can drop notifications from a device that
    /// is no longer theirs.</summary>
    private volatile string? _volumeDeviceId;
    private volatile bool _disposed;
    /// <summary>Makes <see cref="Dispose"/> single-entry: the teardown below is no longer wholly
    /// under <see cref="_lock"/>, so two concurrent callers could otherwise both run it.</summary>
    private int _disposeGate;

    /// <summary>(endpoint id, percent 0-100, muted) after an external change or a
    /// default-device switch. Raised on the notification worker thread, never for this instance's
    /// own sets. The endpoint id identifies WHICH device the notification came from — a
    /// notification from the previous default device can still be in flight when the switch
    /// happens, and applying it to the new device's state would corrupt it.</summary>
    public event Action<string?, int, bool>? Changed;

    /// <summary>The default render endpoint changed (raised BEFORE the accompanying
    /// <see cref="Changed"/>, on the notification worker thread). Both volume modes use this to
    /// swap the active per-device state; the EQ window uses it to re-attach its loopback capture.
    ///
    /// Ordering is guaranteed by the worker being serial and FIFO: both events for one switch are
    /// raised by the same queued action, and a volume notification that arrived before the switch
    /// is raised before it.</summary>
    public event Action? DefaultDeviceChanged;

    public EndpointVolume()
    {
        _volumeCallback = new VolumeCallback(this);
        _notificationClient = new NotificationClient(this);
        // Best-effort eager start so device-change tracking works even before the first
        // set/read; on failure every public method retries via GetVolume().
        lock (_lock)
        {
            EnsureEnumeratorLocked();
        }
    }

    /// <summary>Sets the endpoint volume to the given Windows-slider percent (clamped 0-100).
    /// False when no endpoint is available; the next call retries activation.</summary>
    public bool SetPercent(int percent)
    {
        var volume = GetVolume();
        if (volume is null) return false;
        try
        {
            float scalar = Math.Clamp(percent, 0, 100) / 100f;
            var context = _eventContext;
            if (volume.SetMasterVolumeLevelScalar(scalar, ref context) < 0)
            {
                DropVolume();
                return false;
            }
            return true;
        }
        catch (COMException)
        {
            DropVolume();
            return false;
        }
        catch (InvalidComObjectException)
        {
            return false; // raced Dispose; already released
        }
    }

    /// <summary>Sets the endpoint mute state. Same error contract as <see cref="SetPercent"/>.</summary>
    public bool SetMuted(bool muted)
    {
        var volume = GetVolume();
        if (volume is null) return false;
        try
        {
            var context = _eventContext;
            if (volume.SetMute(muted ? 1 : 0, ref context) < 0)
            {
                DropVolume();
                return false;
            }
            return true;
        }
        catch (COMException)
        {
            DropVolume();
            return false;
        }
        catch (InvalidComObjectException)
        {
            return false;
        }
    }

    /// <summary>Reads the endpoint's current (percent, muted), or null when no endpoint is
    /// available (retried lazily on the next call).</summary>
    public (int Percent, bool Muted)? TryRead()
    {
        var volume = GetVolume();
        if (volume is null) return null;
        try
        {
            if (volume.GetMasterVolumeLevelScalar(out float scalar) < 0)
            {
                DropVolume();
                return null;
            }
            if (volume.GetMute(out int muted) < 0)
            {
                DropVolume();
                return null;
            }
            return (ScalarToPercent(scalar), muted != 0);
        }
        catch (COMException)
        {
            DropVolume();
            return null;
        }
        catch (InvalidComObjectException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Order matters. _disposed first so a notification already on the worker bails instead of
        // re-activating something we are about to release; then the queue, which discards what has
        // not started and waits (bounded) for what has, so the COM teardown below is not racing an
        // action still using these objects.
        if (Interlocked.Exchange(ref _disposeGate, 1) != 0) return;
        _disposed = true;
        _notifications.Dispose();

        // Bounded: the only way the lock is still held here is an action wedged inside a COM call
        // that cannot be cancelled, and shutdown must not hang behind it. Skipping the teardown
        // then leaks a registration into a process that is exiting anyway — the trade this
        // release exists to avoid is the opposite one, a Dispose that never returns.
        if (!Monitor.TryEnter(_lock, DisposeLockTimeout)) return;
        try
        {
            ReleaseVolumeLocked();
            if (_enumerator is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch (COMException) { }
                catch (InvalidComObjectException) { }
                Marshal.ReleaseComObject(_enumerator);
                _enumerator = null;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private static int ScalarToPercent(float scalar) =>
        (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f);

    /// <summary>Creates the device enumerator and registers the endpoint-notification callback,
    /// once. Caller must hold <see cref="_lock"/>. Silent on failure (retried on the next call).</summary>
    private void EnsureEnumeratorLocked()
    {
        if (_enumerator is not null) return;
        try
        {
            var enumerator = (AudioEndpoint.IMMDeviceEnumerator)new AudioEndpoint.MMDeviceEnumerator();
            // Only cache WITH the callback registered: an enumerator cached after a failed
            // registration would work for reads/sets but silently never track default-device
            // changes for the rest of the session.
            if (enumerator.RegisterEndpointNotificationCallback(_notificationClient) < 0)
            {
                Marshal.ReleaseComObject(enumerator);
                return;
            }
            _enumerator = enumerator;
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
    }

    /// <summary>The activated endpoint-volume interface for the CURRENT default render device,
    /// activating (and registering the volume-change callback) lazily. Null when unavailable.</summary>
    private IAudioEndpointVolume? GetVolume()
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (_volume is not null) return _volume;
            EnsureEnumeratorLocked();
            if (_enumerator is null) return null;
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(
                        AudioEndpoint.EDataFlow.Render, AudioEndpoint.ERole.Multimedia, out var device) < 0
                    || device is null)
                    return null;
                IntPtr ptr = IntPtr.Zero;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out ptr) < 0 || ptr == IntPtr.Zero)
                        return null;
                    var volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr);
                    if (volume.RegisterControlChangeNotify(_volumeCallback) < 0)
                    {
                        Marshal.ReleaseComObject(volume);
                        return null;
                    }
                    _volumeDeviceId = ReadDeviceId(device);
                    _volume = volume;
                    return _volume;
                }
                finally
                {
                    if (ptr != IntPtr.Zero) Marshal.Release(ptr);
                    Marshal.ReleaseComObject(device);
                }
            }
            catch (COMException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }
    }

    /// <summary>Releases the activated volume object after a failed call so the next call
    /// re-activates against whatever the (possibly new) default endpoint is.</summary>
    private void DropVolume()
    {
        lock (_lock)
        {
            ReleaseVolumeLocked();
        }
    }

    private static string? ReadDeviceId(AudioEndpoint.IMMDevice device)
    {
        if (device.GetId(out var idPtr) < 0 || idPtr == IntPtr.Zero)
            return null;
        var id = Marshal.PtrToStringUni(idPtr);
        Marshal.FreeCoTaskMem(idPtr);
        return id;
    }

    private void ReleaseVolumeLocked()
    {
        _volumeDeviceId = null;
        if (_volume is null) return;
        try
        {
            _volume.UnregisterControlChangeNotify(_volumeCallback);
        }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    /// <summary>COM CALLBACK THREAD — capture only. <paramref name="data"/> points at memory that
    /// is valid only for the duration of this call, so the struct is read here; the event is raised
    /// on the worker. Swallows the echo of this instance's own sets (matching event context);
    /// everything else is external. No MMDevAPI call may appear in this method.</summary>
    private void OnVolumeNotification(IntPtr data)
    {
        if (data == IntPtr.Zero || _disposed) return;
        AudioVolumeNotificationData notification;
        try
        {
            notification = Marshal.PtrToStructure<AudioVolumeNotificationData>(data);
        }
        catch (ArgumentException)
        {
            return;
        }
        if (notification.EventContext == _eventContext) return;
        // Stamped with the endpoint that was activated when the notification arrived, not with
        // whatever is current by the time the worker gets to it.
        string? deviceId = _volumeDeviceId;
        int percent = ScalarToPercent(notification.MasterVolume);
        bool muted = notification.Muted != 0;
        _notifications.Post(() => Changed?.Invoke(deviceId, percent, muted));
    }

    /// <summary>COM CALLBACK THREAD — hand off and return. Every line of the actual work is in
    /// <see cref="HandleDefaultRenderDeviceChanged"/>, because all of it calls MMDevAPI.</summary>
    private void OnDefaultRenderDeviceChanged()
    {
        if (_disposed) return;
        _notifications.Post(HandleDefaultRenderDeviceChanged);
    }

    /// <summary>Worker thread: re-activate on the new endpoint and report its current state as an
    /// external change. Safe to call MMDevAPI from here — this is not a callback thread.</summary>
    private void HandleDefaultRenderDeviceChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
            ReleaseVolumeLocked(); // the TryRead below activates the new endpoint
        }
        DefaultDeviceChanged?.Invoke();
        // TryRead re-activates on the NEW endpoint first, so _volumeDeviceId names it.
        if (TryRead() is { } state)
            Changed?.Invoke(_volumeDeviceId, state.Percent, state.Muted);
    }

    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly EndpointVolume _owner;
        public VolumeCallback(EndpointVolume owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            _owner.OnVolumeNotification(notifyData);
            return 0;
        }
    }

    private sealed class NotificationClient : AudioEndpoint.IMMNotificationClient
    {
        private readonly EndpointVolume _owner;
        public NotificationClient(EndpointVolume owner) => _owner = owner;

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(
            AudioEndpoint.EDataFlow flow, AudioEndpoint.ERole role, string? defaultDeviceId)
        {
            if (flow == AudioEndpoint.EDataFlow.Render && role == AudioEndpoint.ERole.Multimedia)
                _owner.OnDefaultRenderDeviceChanged();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, AudioEndpoint.PropertyKey key) => 0;
    }

    /// <summary>AUDIO_VOLUME_NOTIFICATION_DATA (endpointvolume.h) minus the variable-length
    /// per-channel tail, which master volume/mute never needs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    // [PreserveSig] everywhere for the same reason documented on AudioEndpoint's interop: real
    // HRESULT returns, and — critically — a correct CCW vtable for the callback interface below.
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int muted, ref Guid eventContext);
        [PreserveSig] int GetMute(out int muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    /// <summary>Implemented by managed code (a CCW handed to RegisterControlChangeNotify).</summary>
    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }
}
