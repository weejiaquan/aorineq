namespace ApoVolume.Core;

/// <summary>The two shipped icon files and which one a mute state shows. The tray swaps between
/// them so the icon itself says whether audio is muted; the exe's Win32 icon and every window's
/// title bar always use the normal art. Both files carry the same frame sizes so the shell can
/// pick a matching one in either state.
///
/// The names live here rather than in the WPF layer so the mute-to-art mapping has exactly one
/// definition and can be exercised without a UI.</summary>
public static class AppIcons
{
    public const string Normal = "ApoVolume.ico";
    public const string Muted = "ApoVolume-muted.ico";

    /// <summary>The icon file for a mute state.</summary>
    public static string FileName(bool muted) => muted ? Muted : Normal;

    /// <summary>The pack:// URI of that file's embedded WPF resource — how the running app loads
    /// it (the .NET SDK does not auto-include .ico as a WPF Resource; the app's csproj has an
    /// explicit &lt;Resource&gt; item per file).</summary>
    public static string ResourceUri(bool muted) => "pack://application:,,,/" + FileName(muted);
}
