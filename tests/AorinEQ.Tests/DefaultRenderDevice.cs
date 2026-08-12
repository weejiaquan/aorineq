using System.Runtime.InteropServices;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Test-only control over which render endpoint Windows treats as the default, so the
/// device-change notification path can be driven for real instead of described.
///
/// It goes through IPolicyConfig — the undocumented interface the Sound control panel itself uses.
/// There is no documented API for this, and there is no reason for the PRODUCT to have one (nothing
/// in AorinEQ ever changes the user's default device), so the interop lives here rather than in
/// Core. It needs no elevation.</summary>
internal static class DefaultRenderDevice
{
    /// <summary>The current default render endpoint id, or null when there is no audio device.</summary>
    public static string? Current => AudioEndpoint.GetDefaultRenderEndpointId();

    /// <summary>The active render endpoints, in enumeration order.</summary>
    public static IReadOnlyList<RenderEndpoint> All => AudioEndpoint.GetRenderEndpoints();

    /// <summary>Makes <paramref name="endpointId"/> the default for all three roles, exactly as
    /// picking it in Sound settings would. Throws when the call fails — a test that cannot switch
    /// the device must fail loudly, not quietly assert nothing.</summary>
    public static void SetDefault(string endpointId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (int role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
            {
                int hr = config.SetDefaultEndpoint(endpointId, role);
                if (hr < 0)
                    throw new InvalidOperationException(
                        $"IPolicyConfig.SetDefaultEndpoint('{endpointId}', role {role}) failed: 0x{hr:X8}");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    /// <summary>The endpoint the machine should be switched TO from <paramref name="current"/> —
    /// the first active render endpoint that is not the current default. Null when the machine has
    /// only one, in which case there is no device change to observe.</summary>
    public static RenderEndpoint? Other(string? current) =>
        All.FirstOrDefault(e => e.Id != current);

    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    // Only SetDefaultEndpoint is used; the members above it exist to place it at the right vtable
    // slot. [PreserveSig] for the same reason as the rest of this repo's COM interop.
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }
}
