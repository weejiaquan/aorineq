using System.Runtime.InteropServices;

namespace ApoVolume.Core;

/// <summary>Minimal MMDevice COM interop: the default render endpoint's device id, whose GUID
/// tail is the key Equalizer APO uses to record per-device installs in the registry.</summary>
public static class AudioEndpoint
{
    /// <summary>Returns the default render endpoint id, e.g.
    /// <c>{0.0.0.00000000}.{9c1af7ff-....}</c>, or null when it can't be determined (no audio
    /// device, audio service down). Never throws.</summary>
    public static string? GetDefaultRenderEndpointId()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
            if (hr != 0 || device is null)
                return null;
            hr = device.GetId(out var idPtr);
            if (hr != 0 || idPtr == IntPtr.Zero)
                return null;
            var id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            return id;
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

    /// <summary>Extracts the endpoint GUID (with braces) from a full endpoint id — the part
    /// Equalizer APO uses as its "Child APOs" subkey name. Null when the id has no GUID tail.</summary>
    public static string? EndpointGuid(string? endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
            return null;
        int brace = endpointId.LastIndexOf('{');
        if (brace < 0 || !endpointId.EndsWith('}'))
            return null;
        var guid = endpointId[brace..];
        return Guid.TryParse(guid, out _) ? guid : null;
    }

    // The interop below is shared with EndpointVolume (internal, one place for the whole
    // MMDevice family). Full member lists where notification callbacks can hand us any value.
    internal enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    internal enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    // [PreserveSig] on every method: the int returns are then REAL HRESULTs in both directions.
    // Without it the CLR applies the HRESULT transform, which happens to be survivable for
    // outgoing (RCW) calls — failures throw COMException, "hr" always reads 0 — but is FATAL for
    // interfaces implemented by managed code (IMMNotificationClient below): the CCW stub expects
    // a phantom retval parameter the native caller never passes, and the write to it crashes the
    // process the moment the first notification arrives.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr @interface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
    }

    /// <summary>Implemented by managed code (a CCW handed to
    /// <see cref="IMMDeviceEnumerator.RegisterEndpointNotificationCallback"/>); MMDevAPI calls in
    /// on its own worker threads.</summary>
    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        // defaultDeviceId is null when the last device of the flow/role goes away.
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FmtId;
        public int Pid;
    }
}
