using System.Runtime.InteropServices;

namespace ApoVolume.Core;

/// <summary>Windows master volume/mute on the default render endpoint, for "system" volume mode.
/// Percent maps 1:1 to the Windows slider scale (scalar × 100 — no taper math). Every set passes
/// this instance's own event-context GUID, and notifications carrying that context are swallowed,
/// so <see cref="Changed"/> only fires for EXTERNAL changes (another app, the Windows mixer, a
/// device switch). <see cref="Changed"/> is raised on a COM callback thread — UI callers must
/// marshal (same contract as KeyboardHook's events).
///
/// Error contract: never throws to callers. Device loss / audio service down make methods return
/// false/null, and the next call lazily retries activation (a default-device change also forces
/// re-activation onto the new endpoint). Dispose unregisters both callbacks and releases COM.</summary>
public sealed class EndpointVolume : IDisposable
{
    private const int ClsCtxAll = 0x17; // CLSCTX_ALL

    private readonly object _lock = new();
    private readonly Guid _eventContext = Guid.NewGuid();
    private readonly VolumeCallback _volumeCallback;
    private readonly NotificationClient _notificationClient;
    private AudioEndpoint.IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private bool _disposed;

    /// <summary>(percent 0-100, muted) after an external change or a default-device switch.
    /// Raised on a COM callback thread, never for this instance's own sets.</summary>
    public event Action<int, bool>? Changed;

    /// <summary>The default render endpoint changed (raised BEFORE the accompanying
    /// <see cref="Changed"/>, on a COM callback thread). Both volume modes use this to swap
    /// the active per-device state; the EQ window uses it to re-attach its loopback capture.</summary>
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
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
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

    private void ReleaseVolumeLocked()
    {
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

    /// <summary>COM callback thread: an endpoint volume/mute notification arrived. Swallows the
    /// echo of this instance's own sets (matching event context); everything else is external.</summary>
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
        Changed?.Invoke(ScalarToPercent(notification.MasterVolume), notification.Muted != 0);
    }

    /// <summary>COM callback thread: the default render endpoint changed. Re-activates on the
    /// new endpoint and reports its current state as an external change.</summary>
    private void OnDefaultRenderDeviceChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
            ReleaseVolumeLocked(); // the next GetVolume() activates the new endpoint
        }
        DefaultDeviceChanged?.Invoke();
        if (TryRead() is { } state)
            Changed?.Invoke(state.Percent, state.Muted);
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
