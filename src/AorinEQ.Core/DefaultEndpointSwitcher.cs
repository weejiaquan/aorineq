using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Windows' default playback device per role, as it was before AorinEQ moved it.
/// Persisted in <see cref="Settings.AirPlayRestoreDefault"/> BEFORE the switch, so a killed
/// process can still put it back on the next start.</summary>
public sealed record DefaultRenderRecord(string? Console, string? Multimedia, string? Communications);

/// <summary>What a default-device notification means while AirPlay only may be managing it.</summary>
public enum DefaultChange
{
    /// <summary>AorinEQ's own switch (to the cable, or back) landing. No notice, no repair.</summary>
    Ours,
    /// <summary>The user chose another output mid-stream: stop managing it, keep streaming.</summary>
    UserOverride,
    /// <summary>VB-Cable went away mid-stream: put the user's devices back now.</summary>
    CableLost,
    /// <summary>Nothing of AorinEQ's is involved.</summary>
    Unrelated,
}

/// <summary>Moves Windows' default render endpoint and puts it back, through IPolicyConfig — the
/// undocumented interface the Sound control panel itself uses (as EarTrumpet and SoundSwitch do).
/// There is no documented API for this. It needs no elevation.
///
/// This interop used to live in the TEST project, because nothing in the product changed the
/// user's default device. AirPlay only does, so it moved here and the test helper calls it.</summary>
public static class DefaultEndpointSwitcher
{
    private static readonly AudioEndpoint.ERole[] Roles =
        [AudioEndpoint.ERole.Console, AudioEndpoint.ERole.Multimedia, AudioEndpoint.ERole.Communications];

    public static DefaultRenderRecord Capture() => new(
        AudioEndpoint.GetDefaultRenderEndpointId(AudioEndpoint.ERole.Console),
        AudioEndpoint.GetDefaultRenderEndpointId(AudioEndpoint.ERole.Multimedia),
        AudioEndpoint.GetDefaultRenderEndpointId(AudioEndpoint.ERole.Communications));

    /// <summary>Makes <paramref name="endpointId"/> the default for all three roles, exactly as
    /// picking it in Sound settings would. False when any role refused.</summary>
    public static bool SetDefaultForAllRoles(string endpointId)
    {
        bool all = true;
        foreach (var role in Roles)
            all &= SetDefault(endpointId, role);
        return all;
    }

    internal static bool SetDefault(string endpointId, AudioEndpoint.ERole role)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                return config.SetDefaultEndpoint(endpointId, role) >= 0;
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Puts each role back to its recorded endpoint when that endpoint is still active;
    /// a role whose device has gone is left on whatever Windows chose. Returns the endpoint the
    /// Multimedia default moved to, or null when it did not move — the caller flags that one
    /// notification as its own.</summary>
    public static string? Restore(DefaultRenderRecord record)
    {
        var active = AudioEndpoint.GetRenderEndpoints()
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = AudioEndpoint.GetDefaultRenderEndpointId();
        foreach (var (role, id) in new[]
                 {
                     (AudioEndpoint.ERole.Console, record.Console),
                     (AudioEndpoint.ERole.Multimedia, record.Multimedia),
                     (AudioEndpoint.ERole.Communications, record.Communications),
                 })
        {
            if (id is not null && active.Contains(id))
                SetDefault(id, role);
        }
        var after = AudioEndpoint.GetDefaultRenderEndpointId();
        return after is not null && !string.Equals(after, before, StringComparison.OrdinalIgnoreCase)
            ? after
            : null;
    }

    /// <summary>Reads one default-device notification. <paramref name="ownTargetId"/> is the
    /// endpoint AorinEQ's last switch moved to and has not yet seen land; <paramref name="record"/>
    /// is non-null while AirPlay only is managing the default; <paramref name="cableId"/> is the
    /// ACTIVE VB-Cable endpoint, null when it is gone.</summary>
    public static DefaultChange Classify(string? newDefaultId, string? ownTargetId,
        DefaultRenderRecord? record, string? cableId)
    {
        if (ownTargetId is not null && string.Equals(newDefaultId, ownTargetId, StringComparison.OrdinalIgnoreCase))
            return DefaultChange.Ours;
        if (record is null)
            return DefaultChange.Unrelated;
        if (cableId is null)
            return DefaultChange.CableLost;
        return string.Equals(newDefaultId, cableId, StringComparison.OrdinalIgnoreCase)
            ? DefaultChange.Ours
            : DefaultChange.UserOverride;
    }

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
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, AudioEndpoint.ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }
}
