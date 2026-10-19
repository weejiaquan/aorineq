using System.Runtime.InteropServices;
using System.Text;

namespace AorinEQ.Core;

/// <summary>What the AirPlay page shows about VB-Cable. Ordered by precedence in
/// <see cref="VirtualCable.StateFor"/>.</summary>
public enum VirtualCableState
{
    Missing,
    Inactive,
    PendingReboot,
    Present,
}

/// <summary>VB-Audio Virtual Cable on this machine.
///
/// Matched by the hardware id VB-Audio's INF gives the device (<see cref="HardwareId"/>), read from
/// the device node BEHIND the endpoint: the endpoint's own property store does not carry hardware
/// ids (PKEY_Device_HardwareIds is empty there for every endpoint, measured 2026-10-18), but it
/// does name its parent device instance, and the configuration manager answers for that. The
/// friendly name is never consulted — users rename endpoints, and anyone can call theirs "CABLE
/// Input".</summary>
public static class VirtualCable
{
    /// <summary>The hardware id in VB-Audio's INF (<c>HardwareId="VBAudioVACWDM"</c>).</summary>
    public const string HardwareId = "VBAudioVACWDM";

    /// <summary>VB-Audio's own page for the cable — the package source, the fallback when anything
    /// goes wrong, and the donation link.</summary>
    public const string VendorPage = "https://vb-audio.com/Cable/";

    /// <summary>The ACTIVE render endpoint backed by VB-Cable ("CABLE Input"), or null.</summary>
    public static RenderEndpoint? Find() => AudioEndpoint.GetRenderEndpoints().FirstOrDefault(IsCable);

    /// <summary>EVERY active render endpoint on VB-Cable's device: a pack can expose more than one
    /// (e.g. "CABLE In 16ch" beside "CABLE Input"). For telling whether a default IS the cable;
    /// the endpoint a stream captures is still <see cref="Find"/>'s.</summary>
    public static IReadOnlyList<RenderEndpoint> FindAll() => AudioEndpoint.GetRenderEndpoints().Where(IsCable).ToList();

    /// <summary>Whether a VB-Cable endpoint exists but is disabled in Sound settings or has its
    /// device disabled — so the page can say "turn it on" instead of offering a reinstall. An
    /// uninstalled cable's leftover not-present endpoints do NOT count: their device node is gone,
    /// so no hardware id can be read for them.</summary>
    public static bool IsInstalledButInactive() => AudioEndpoint.GetInactiveRenderEndpoints().Any(IsCable);

    public static bool IsCable(RenderEndpoint endpoint) =>
        HardwareIdsOf(endpoint.Id).Contains(HardwareId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The hardware ids of the device behind an endpoint; empty when unreadable.</summary>
    public static IReadOnlyList<string> HardwareIdsOf(string endpointId) =>
        DevNodeHardwareIds(AudioEndpoint.GetDeviceInstanceId(endpointId));

    /// <summary>CM_DRP_HARDWAREID of a PRESENT device node (a device disabled in Device Manager is
    /// still present; an uninstalled one is not). Empty on any failure. Never throws.</summary>
    public static IReadOnlyList<string> DevNodeHardwareIds(string? deviceInstanceId)
    {
        if (string.IsNullOrEmpty(deviceInstanceId))
            return [];
        if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, LocateDevNodeNormal) != CrSuccess)
            return [];
        uint length = 0;
        if (CM_Get_DevNode_Registry_PropertyW(devInst, DrpHardwareId, out _, null, ref length, 0) != CrBufferSmall
            || length == 0)
            return [];
        var buffer = new byte[length];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, DrpHardwareId, out _, buffer, ref length, 0) != CrSuccess)
            return [];
        return Encoding.Unicode.GetString(buffer, 0, (int)length)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>One answer for the page: a working cable wins; then a pending reboot (the install
    /// ran, Windows has not caught up); then a cable that exists but is switched off.</summary>
    public static VirtualCableState StateFor(bool found, bool installedButInactive, bool pendingReboot) =>
        found ? VirtualCableState.Present
        : pendingReboot ? VirtualCableState.PendingReboot
        : installedButInactive ? VirtualCableState.Inactive
        : VirtualCableState.Missing;

    private const int CrSuccess = 0x0;
    private const int CrBufferSmall = 0x1A;
    private const uint LocateDevNodeNormal = 0x0; // CM_LOCATE_DEVNODE_NORMAL: present nodes only
    private const uint DrpHardwareId = 0x2;       // CM_DRP_HARDWAREID (REG_MULTI_SZ)

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regDataType, byte[]? buffer, ref uint length, uint flags);
}
