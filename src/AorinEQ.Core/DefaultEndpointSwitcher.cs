using System.Diagnostics;
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

/// <summary>How <see cref="DefaultEndpointSwitcher.BeginAirPlayOnly"/> ended.</summary>
public enum AirPlayOnlyStart
{
    /// <summary>The cable already was the default: nothing recorded, nothing moved.</summary>
    AlreadyDefault,
    /// <summary>The record did not reach the disk, so the default was left alone.</summary>
    NotSaved,
    /// <summary>The default is on the cable and the record is held.</summary>
    Switched,
    /// <summary>Windows refused the switch; anything that moved was put back.</summary>
    Refused,
}

/// <summary>The sets a restore will make, and whether they cover every role that needs one.</summary>
internal sealed record RestorePlan(IReadOnlyList<(AudioEndpoint.ERole Role, string EndpointId)> Sets, bool Complete);

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

    /// <summary>One role's switch. False when Windows refused it; the HRESULT and the role are
    /// traced, since the callers only see the bool.</summary>
    internal static bool SetDefault(string endpointId, AudioEndpoint.ERole role)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr = config.SetDefaultEndpoint(endpointId, role);
                if (hr < 0)
                    Trace.TraceWarning($"DefaultEndpointSwitcher: SetDefaultEndpoint('{endpointId}', {role}) failed with 0x{hr:X8}");
                return hr >= 0;
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }
        }
        catch (COMException ex)
        {
            Trace.TraceWarning($"DefaultEndpointSwitcher: SetDefaultEndpoint('{endpointId}', {role}) failed with 0x{ex.HResult:X8}");
            return false;
        }
        catch (InvalidCastException ex)
        {
            Trace.TraceWarning($"DefaultEndpointSwitcher: IPolicyConfig unavailable for {role}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Puts each role back as <see cref="PlanRestore"/> decides, against the endpoints
    /// Windows has right now. <c>Complete</c> is false when the plan could not cover every role or
    /// Windows refused any set: the caller then keeps the record and tries again at the next end
    /// or start. <c>MovedTo</c> is the endpoint the Multimedia default moved to, or null when it
    /// did not move; the caller flags that one notification as its own.</summary>
    public static (bool Complete, string? MovedTo) Restore(DefaultRenderRecord record)
    {
        // One enumeration for both lists, so the cable set describes the same moment as the rest.
        var active = AudioEndpoint.GetRenderEndpoints();
        var plan = PlanRestore(record,
            active.Select(e => e.Id).ToList(),
            Capture(),
            active.Where(VirtualCable.IsCable).Select(e => e.Id).ToList());
        var before = AudioEndpoint.GetDefaultRenderEndpointId();
        bool complete = Apply(plan);
        var after = AudioEndpoint.GetDefaultRenderEndpointId();
        return (complete, after is not null && !string.Equals(after, before, StringComparison.OrdinalIgnoreCase)
            ? after
            : null);
    }

    /// <summary>What putting <paramref name="record"/> back means, role by role, given the ACTIVE
    /// render endpoints (in Windows' order), the current default per role and EVERY active cable
    /// endpoint (a VB-Cable pack can put more than one on its device, e.g. "CABLE In 16ch"; empty
    /// when there is none):
    /// <list type="bullet">
    /// <item>the recorded endpoint is active: that role goes back to it;</item>
    /// <item>it is gone and the role is on something other than a cable endpoint: Windows' choice
    /// stays;</item>
    /// <item>it is gone and the role is still on a cable endpoint: the first active endpoint that is
    /// none of them, so nobody is left playing into a device with no speakers; with no such
    /// endpoint the plan is incomplete.</item>
    /// </list>
    /// An empty endpoint list is also what a failed enumeration returns, so it never reads as
    /// "everything is gone": the plan is empty and incomplete, and the record is kept.</summary>
    internal static RestorePlan PlanRestore(DefaultRenderRecord record, IReadOnlyList<string> activeIds,
        DefaultRenderRecord current, IReadOnlyCollection<string> cableIds)
    {
        if (activeIds.Count == 0)
            return new RestorePlan([], Complete: false);
        var fallback = activeIds.FirstOrDefault(id => !IsCableId(id, cableIds));
        var sets = new List<(AudioEndpoint.ERole, string)>();
        bool complete = true;
        foreach (var (role, recorded, now) in new[]
                 {
                     (AudioEndpoint.ERole.Console, record.Console, current.Console),
                     (AudioEndpoint.ERole.Multimedia, record.Multimedia, current.Multimedia),
                     (AudioEndpoint.ERole.Communications, record.Communications, current.Communications),
                 })
        {
            if (recorded is not null && activeIds.Contains(recorded, StringComparer.OrdinalIgnoreCase))
                sets.Add((role, recorded));
            else if (IsCableId(now, cableIds))
            {
                if (fallback is null) complete = false;
                else sets.Add((role, fallback));
            }
        }
        return new RestorePlan(sets, complete);
    }

    /// <summary>Carries out a plan. True only when the plan was complete AND Windows accepted
    /// every set in it.</summary>
    internal static bool Apply(RestorePlan plan)
    {
        bool all = plan.Complete;
        foreach (var (role, endpointId) in plan.Sets)
            all &= SetDefault(endpointId, role);
        return all;
    }

    /// <summary>The one restore every end of AirPlay only goes through: Disconnect, a dropped
    /// receiver, the app exiting, the cable vanishing, a refused switch, AirPlay only turned off,
    /// and the NEXT START after a crash (a record still in settings.json means the last session
    /// died with the default on the cable). Puts each role back (see <see cref="Restore"/>) and
    /// returns the settings without the record, for the caller to save, plus the endpoint the
    /// Multimedia default moved to. A restore that was not complete keeps the record (same
    /// instance), so the next end or start tries again instead of forgetting what to put back.
    /// Settings with no record come back as the same instance and nothing moves. The restore
    /// happens BEFORE the caller persists the cleared record: dying between the two restores
    /// twice, which is harmless, instead of not at all.</summary>
    public static (Settings Settings, string? MovedTo) RestoreRecorded(Settings settings)
    {
        if (settings.AirPlayRestoreDefault is not { } record)
            return (settings, null);
        var (complete, movedTo) = Restore(record);
        return (complete ? settings with { AirPlayRestoreDefault = null } : settings, movedTo);
    }

    /// <summary>Starts AirPlay only's hold on the default, in the one order that survives a kill
    /// at any instruction: capture the defaults, <paramref name="persist"/> them (true only once
    /// they are on disk), and only then switch all three roles to <paramref name="cableId"/>.
    /// Returns the settings the caller holds from now on.
    /// <list type="bullet">
    /// <item><see cref="AirPlayOnlyStart.AlreadyDefault"/>: the cable already is the default
    /// (the user's choice, or a reconnect mid-stream): nothing recorded, nothing moved.</item>
    /// <item><see cref="AirPlayOnlyStart.NotSaved"/>: the record could not be written, so the
    /// default is NOT moved (a crash would otherwise strand the user on the cable). The settings
    /// come back without a record.</item>
    /// <item><see cref="AirPlayOnlyStart.Switched"/>: the default is on the cable and the record
    /// is held. A record already held (reconnecting) is kept, not re-captured: it describes the
    /// user's devices, and capturing now could record the cable.</item>
    /// <item><see cref="AirPlayOnlyStart.Refused"/>: Windows refused a role; whatever did move is
    /// put back through <see cref="RestoreRecorded"/>, whose settings and <c>MovedTo</c> come back
    /// for the caller to save and flag.</item>
    /// </list></summary>
    public static (Settings Settings, AirPlayOnlyStart Outcome, string? MovedTo) BeginAirPlayOnly(
        Settings settings, string cableId, Func<Settings, bool> persist)
    {
        if (string.Equals(AudioEndpoint.GetDefaultRenderEndpointId(), cableId, StringComparison.OrdinalIgnoreCase))
            return (settings, AirPlayOnlyStart.AlreadyDefault, null);
        if (settings.AirPlayRestoreDefault is null)
        {
            var recorded = settings with { AirPlayRestoreDefault = Capture() };
            if (!persist(recorded))
                return (settings, AirPlayOnlyStart.NotSaved, null);
            settings = recorded;
        }
        if (SetDefaultForAllRoles(cableId))
            return (settings, AirPlayOnlyStart.Switched, null);
        var (restored, movedTo) = RestoreRecorded(settings);
        return (restored, AirPlayOnlyStart.Refused, movedTo);
    }

    /// <summary>Whether this process may put Windows' default back (and save what that clears):
    /// only the running instance, which holds the single-instance mutex, and only once it has
    /// adopted settings.json as its own (its per-device state is loaded). A second launch, the
    /// non-elevated half of the elevation bounce and a declined first-run wizard all exit without
    /// one or both: restoring there would pull the default out from under the stream the REAL
    /// instance is running, and the save behind it would write defaults over the user's settings.</summary>
    public static bool MayRestore(bool ownsInstance, bool settingsAdopted) => ownsInstance && settingsAdopted;

    /// <summary>Whether a reading of <paramref name="endpointGuid"/> describes VB-Cable (any of its
    /// active endpoints, <paramref name="cableGuids"/>) while AorinEQ's own AirPlay-only switch
    /// holds the default there (<paramref name="record"/> set).
    /// Such a reading is AorinEQ's doing, not news: the health monitor keeps it from its tracker, so
    /// there is no "Equalizer APO isn't running" balloon on every stream, no automatic-repair prompt
    /// for the cable, and no "working again" balloon when the default comes back.</summary>
    public static bool IsHeldOnCable(DefaultRenderRecord? record, string? endpointGuid,
        IReadOnlyCollection<string> cableGuids) =>
        record is not null && IsCableId(endpointGuid, cableGuids);

    /// <summary>Whether <paramref name="id"/> (an endpoint id or GUID) is one of the cable's.</summary>
    private static bool IsCableId(string? id, IReadOnlyCollection<string> cableIds) =>
        id is not null && cableIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads one default-device notification. <paramref name="ownTargetId"/> is the
    /// endpoint AorinEQ's last switch moved to and has not yet seen land; <paramref name="record"/>
    /// is non-null while AirPlay only is managing the default; <paramref name="cableIds"/> are the
    /// ACTIVE VB-Cable endpoints (every one on its device), empty when it is gone.</summary>
    public static DefaultChange Classify(string? newDefaultId, string? ownTargetId,
        DefaultRenderRecord? record, IReadOnlyCollection<string> cableIds)
    {
        if (ownTargetId is not null && string.Equals(newDefaultId, ownTargetId, StringComparison.OrdinalIgnoreCase))
            return DefaultChange.Ours;
        if (record is null)
            return DefaultChange.Unrelated;
        if (cableIds.Count == 0)
            return DefaultChange.CableLost;
        return IsCableId(newDefaultId, cableIds) ? DefaultChange.Ours : DefaultChange.UserOverride;
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
