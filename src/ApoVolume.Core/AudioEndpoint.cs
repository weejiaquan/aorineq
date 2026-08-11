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

    private enum EDataFlow
    {
        Render = 0,
    }

    private enum ERole
    {
        Multimedia = 1,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr @interface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId(out IntPtr id);
    }
}
