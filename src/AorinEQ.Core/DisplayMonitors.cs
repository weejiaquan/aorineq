using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Enumerates the machine's screens as <see cref="HudMonitor"/>s, with the stable device
/// path the HUD remembers widgets against.
///
/// IDENTITY IS THE PROBLEM THIS SOLVES. EnumDisplayMonitors gives back
/// <c>\\.\DISPLAY1</c>-style names, which are POSITIONS in the current arrangement: unplug a
/// monitor and the one that was DISPLAY2 becomes DISPLAY1, and every widget remembered against a
/// name follows the slot rather than the screen. EnumDisplayDevices with
/// EDD_GET_DEVICE_INTERFACE_NAME instead returns the monitor's device interface path — which
/// encodes the hardware, survives docking and re-cabling, and is what the widgets are keyed on.
///
/// THE FALLBACK CHAIN MATTERS, because getting it wrong reintroduces the very bug the device path
/// exists to avoid. Interface path first; then the monitor's plain hardware id (MONITOR\GSM5B09\
/// {...}, also hardware-derived and also stable); and only if BOTH are unavailable, the adapter
/// name. That last one really IS the arrangement slot, so a widget on a display that declines to
/// identify itself at all can still follow the slot rather than the screen. It is the last
/// resort, not the fallback.
///
/// GEOMETRY IS IN PHYSICAL PIXELS at the API boundary and in DIPs everywhere above it. The
/// process is PerMonitorV2 (see app.manifest), so the two differ per screen; the caller supplies
/// the scale because only the UI layer can ask WPF what it is.</summary>
public static class DisplayMonitors
{
    /// <summary>Every screen, in physical pixels. The UI layer converts to DIPs.</summary>
    public static IReadOnlyList<HudMonitor> Enumerate()
    {
        var result = new List<HudMonitor>();
        var paths = DeviceInterfacePaths();

        bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                string adapter = info.szDevice ?? "";
                string id = paths.TryGetValue(adapter, out var path) && !string.IsNullOrEmpty(path)
                    ? path
                    : adapter;
                result.Add(new HudMonitor(
                    DeviceId: id,
                    Name: FriendlyName(adapter),
                    Bounds: ToRect(info.rcMonitor),
                    WorkArea: ToRect(info.rcWork),
                    IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true; // keep enumerating even if one monitor failed to describe itself
        }, IntPtr.Zero);

        // A failed enumeration is reported as "no screens": the caller (HudPlacement.TryResolve)
        // already treats that as "nowhere to put it" rather than inventing a rectangle.
        return ok ? result : [];
    }

    private static HudRect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>Adapter name (<c>\\.\DISPLAY1</c>) to the attached monitor's most stable
    /// identifier. Two passes because the identity lives on the MONITOR device, which is a child
    /// of the adapter: the interface path first, and the plain hardware id when Windows will not
    /// give one. Both describe the hardware; neither moves when the arrangement does.</summary>
    private static Dictionary<string, string> DeviceInterfacePaths()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                string id = MonitorId(adapter.DeviceName, EDD_GET_DEVICE_INTERFACE_NAME);
                if (string.IsNullOrEmpty(id))
                    id = MonitorId(adapter.DeviceName, 0);
                if (!string.IsNullOrEmpty(id))
                    map[adapter.DeviceName] = id;
            }
            adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return map;
    }

    /// <summary>The monitor device's id under <paramref name="flags"/>, or "" if it has none.</summary>
    private static string MonitorId(string adapterName, uint flags)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(adapterName, 0, ref monitor, flags)
            ? monitor.DeviceID ?? ""
            : "";
    }

    /// <summary>What to call a screen in a menu: the monitor's own description where Windows has
    /// one, otherwise the adapter name it is attached to.</summary>
    private static string FriendlyName(string adapter)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(adapter, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString))
            return monitor.DeviceString!;
        return string.IsNullOrEmpty(adapter) ? "Display" : adapter;
    }

    private const int MONITORINFOF_PRIMARY = 0x1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex,
        ref DISPLAY_DEVICE displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // CharSet.Unicode throughout: the ANSI entry points truncate a device path at the first null
    // of a UTF-16 string, which is the v3.0.0 lesson about GetWindowTextW all over again.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
