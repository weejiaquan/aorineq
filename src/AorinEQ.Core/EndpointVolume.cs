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
    /// <summary>Created fresh for each activation and holding the endpoint id it was registered
    /// against, so a notification identifies its OWN device instead of reading whichever device is
    /// current by the time it is handled — during a switch those are not the same device.</summary>
    private VolumeCallback? _volumeCallback;
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
    /// happens, and applying it to the new device's state would corrupt it.
    ///
    /// The id is never null: an endpoint this class could not identify is never activated (see
    /// <see cref="GetActivation"/>), so there is no such thing here as a reading that cannot be
    /// attributed to a device.</summary>
    public event Action<string, int, bool>? Changed;

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
        _notificationClient = new NotificationClient(this);
        // Best-effort eager start so device-change tracking works even before the first
        // set/read; on failure every public method retries via GetActivation().
        lock (_lock)
        {
            EnsureEnumeratorLocked();
        }
    }

    /// <summary>Sets the endpoint volume to the given Windows-slider percent (clamped 0-100).
    /// False when no endpoint is available; the next call retries activation.</summary>
    public bool SetPercent(int percent)
    {
        var volume = GetActivation()?.Volume;
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
        var volume = GetActivation()?.Volume;
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
    public (int Percent, bool Muted)? TryRead() =>
        TryReadStamped() is { } read ? (read.Percent, read.Muted) : null;

    /// <summary>The same read, carrying the endpoint id of the activation it actually came from.
    ///
    /// The id and the volume object are taken together under the lock, so the two always describe
    /// the SAME activation. Reading <see cref="_volumeDeviceId"/> afterwards instead would be a
    /// race: another thread's failed call can <see cref="DropVolume"/> and re-activate between the
    /// read and the stamp, and the reading would go out labelled with a device it did not come
    /// from — or with none at all.</summary>
    private (string DeviceId, int Percent, bool Muted)? TryReadStamped()
    {
        var activation = GetActivation();
        if (activation is not var (volume, deviceId)) return null;
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
            return (deviceId, ScalarToPercent(scalar), muted != 0);
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

        // Bounded: with the queue stopped, the only way the lock is still held here is an action
        // wedged inside a COM call that cannot be cancelled, and shutdown must not hang behind it.
        // Skipping the teardown then leaks a registration and two RCWs — into a process that is on
        // its way out, since this type is disposed at exit. Handing the teardown to a background
        // thread instead would not recover them either: that thread would wait on the same lock the
        // same wedged call is holding, and park forever. A Dispose that always returns is worth
        // more than a teardown that sometimes cannot happen; a Dispose that never returns is the
        // bug this release exists to fix.
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

    /// <summary>The endpoint-volume interface for the CURRENT default render device TOGETHER WITH
    /// the id of the endpoint it belongs to, activating (and registering the volume-change
    /// callback) lazily. Null when unavailable.
    ///
    /// The pair is returned, rather than the volume alone plus a field read afterwards, so callers
    /// cannot accidentally label a reading with a different activation than it came from.
    ///
    /// AN ENDPOINT WHOSE ID CANNOT BE READ IS NOT ACTIVATED. Everything built on this class is
    /// per-device — which device a notification belongs to decides whose saved volume it becomes —
    /// so an endpoint that cannot say what it is cannot take part. Failing here keeps the lazy
    /// retry contract (the next call tries again) instead of producing a stream of readings nobody
    /// downstream is able to attribute.</summary>
    private (IAudioEndpointVolume Volume, string DeviceId)? GetActivation()
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (_volume is not null && _volumeDeviceId is not null) return (_volume, _volumeDeviceId);
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
                    // Read the id BEFORE registering: the callback must be able to name its own
                    // endpoint from the moment it can fire.
                    if (ReadDeviceId(device) is not { } deviceId)
                        return null;
                    var volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr);
                    var callback = new VolumeCallback(this, deviceId);
                    if (volume.RegisterControlChangeNotify(callback) < 0)
                    {
                        Marshal.ReleaseComObject(volume);
                        return null;
                    }
                    _volumeCallback = callback;
                    _volumeDeviceId = deviceId;
                    _volume = volume;
                    return (volume, deviceId);
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
            // The exact instance that was registered for THIS activation, not a shared one.
            if (_volumeCallback is not null)
                _volume.UnregisterControlChangeNotify(_volumeCallback);
        }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
        Marshal.ReleaseComObject(_volume);
        _volume = null;
        _volumeCallback = null;
    }

    /// <summary>COM CALLBACK THREAD — capture only. <paramref name="data"/> points at memory that
    /// is valid only for the duration of this call, so the struct is read here; the event is raised
    /// on the worker. Swallows the echo of this instance's own sets (matching event context);
    /// everything else is external. No MMDevAPI call may appear in this method.</summary>
    /// <param name="deviceId">The endpoint this callback was registered against, captured at
    /// activation. NOT re-read from the instance: a notification that was already travelling when
    /// the default device changed would otherwise be stamped with the device that replaced it, and
    /// the old device's volume would be written into the new one's state.</param>
    private void OnVolumeNotification(IntPtr data, string deviceId)
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
        int percent = ScalarToPercent(notification.MasterVolume);
        bool muted = notification.Muted != 0;
        _notifications.Post(() =>
        {
            if (_disposed) return;
            Changed?.Invoke(deviceId, percent, muted);
        });
    }

    /// <summary>COM CALLBACK THREAD — hand off and return. Every line of the actual work is in
    /// <see cref="HandleDefaultRenderDeviceChanged"/>, because all of it calls MMDevAPI.
    ///
    /// The endpoint id the callback carries is deliberately NOT forwarded, and the handler re-reads
    /// the CURRENT default instead. This is convergence, not carelessness: if the device changes
    /// A -> B -> C faster than the queue drains, activating the B the notification named would put
    /// the app on a device the user has already left, and it would stay there. Re-reading means
    /// every queued action moves toward the truth and the last one is always right — and because
    /// the queue drops nothing, there is always a last one. What is lost is only the intermediate
    /// hop, which nothing wants.</summary>
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
        // Re-checked before each hand-off outwards: Dispose only waits a bounded time for an
        // action that has already started, so this one can still be running after it returned.
        if (_disposed) return;
        DefaultDeviceChanged?.Invoke();
        if (_disposed) return;
        // Stamped from the activation the reading came from, not from a field read afterwards.
        if (TryReadStamped() is { } state)
            Changed?.Invoke(state.DeviceId, state.Percent, state.Muted);
    }

    /// <summary>One per activation, carrying the endpoint id it was registered against.</summary>
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly EndpointVolume _owner;
        private readonly string _deviceId;

        public VolumeCallback(EndpointVolume owner, string deviceId)
        {
            _owner = owner;
            _deviceId = deviceId;
        }

        public int OnNotify(IntPtr notifyData)
        {
            _owner.OnVolumeNotification(notifyData, _deviceId);
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
