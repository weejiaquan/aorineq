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

    /// <summary>Switches the default and does not return until the machine has gone quiet again:
    /// the id has actually changed AND no further device notification has arrived for
    /// <see cref="QuietPeriod"/>.
    ///
    /// Every test here leaves through this. One switch produces SEVERAL notifications, and they
    /// arrive asynchronously, so a test that returns the moment the id flips leaves its own tail
    /// running into whatever executes next — where a notification stamped with the device the
    /// PREVIOUS test was on arrives in the middle of this one's assertions. That is not the
    /// product being wrong; it is one test's exhaust landing in another's measurement.</summary>
    public static void SetDefaultAndSettle(string endpointId)
    {
        using var watcher = new EndpointVolume();
        long lastEventTicks = DateTime.UtcNow.Ticks;
        watcher.DefaultDeviceChanged += () =>
            Interlocked.Exchange(ref lastEventTicks, DateTime.UtcNow.Ticks);

        SetDefault(endpointId);

        var deadline = DateTime.UtcNow + SettleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var quietFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastEventTicks), DateTimeKind.Utc);
            if (Current == endpointId && quietFor >= QuietPeriod)
                return;
            Thread.Sleep(25);
        }
        throw new TimeoutException(
            $"the default device did not settle on '{endpointId}' within {SettleTimeout.TotalSeconds:F0}s "
            + $"(it now reads '{Current}')");
    }

    /// <summary>How long without a device notification counts as settled. Notifications for one
    /// switch land within a few ms of each other (measured), so this is many times the gap.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

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
