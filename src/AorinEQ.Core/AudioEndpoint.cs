using System.Runtime.InteropServices;

namespace AorinEQ.Core;

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

    /// <summary>Enumerates the ACTIVE render endpoints with their display names (the EQ
    /// editor's device tabs). Best-effort like the default-endpoint read: failures shrink the
    /// list (possibly to empty) instead of throwing.</summary>
    public static IReadOnlyList<RenderEndpoint> GetRenderEndpoints()
    {
        var result = new List<RenderEndpoint>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out var collection) < 0
                    || collection is null)
                    return result;
                try
                {
                    if (collection.GetCount(out int count) < 0)
                        return result;
                    for (int i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var device) < 0 || device is null)
                            continue;
                        try
                        {
                            if (device.GetId(out var idPtr) < 0 || idPtr == IntPtr.Zero)
                                continue;
                            var id = Marshal.PtrToStringUni(idPtr);
                            Marshal.FreeCoTaskMem(idPtr);
                            if (id is null || EndpointGuid(id) is not { } guid)
                                continue;
                            result.Add(new RenderEndpoint(id, guid, ReadFriendlyName(device) ?? guid));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        return result;
    }

    /// <summary>PKEY_Device_FriendlyName via the endpoint's property store, e.g.
    /// "Speakers (Realtek High Definition Audio)". Null when unreadable.</summary>
    private static string? ReadFriendlyName(IMMDevice device)
    {
        const int stgmRead = 0;
        if (device.OpenPropertyStore(stgmRead, out var storePtr) < 0 || storePtr == IntPtr.Zero)
            return null;
        try
        {
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
            try
            {
                var key = new PropertyKey { FmtId = PkeyDeviceFriendlyNameFmtId, Pid = 14 };
                if (store.GetValue(ref key, out var value) < 0)
                    return null;
                try
                {
                    return value.Vt == VtLpwstr && value.Data != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.Data)
                        : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
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
        finally
        {
            Marshal.Release(storePtr);
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

    private const int DeviceStateActive = 0x1; // DEVICE_STATE_ACTIVE
    private const ushort VtLpwstr = 31;        // VT_LPWSTR
    private static readonly Guid PkeyDeviceFriendlyNameFmtId =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

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
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection? devices);
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

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FmtId;
        public int Pid;
    }

    /// <summary>PROPVARIANT trimmed to the header + first pointer-sized data slot — all this
    /// interop reads is VT_LPWSTR, whose string pointer lives in that slot. Sized out to the
    /// full 16-byte data area so the callee never writes past the struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Data;
        public IntPtr Data2;
    }
}

/// <summary>One active render endpoint: full id (settings key), braced GUID (EAPO's Device
/// guard / Child APOs key), and the human display name for UI.</summary>
public sealed record RenderEndpoint(string Id, string Guid, string FriendlyName);
