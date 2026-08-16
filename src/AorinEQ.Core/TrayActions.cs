namespace AorinEQ.Core;

/// <summary>What a mouse button does when clicked on the tray icon, spelled once.
///
/// Each name is a contract between three things that must agree: the value persisted in
/// settings.json, the item the Settings window's combo shows for it, and the branch the app runs
/// when the click arrives. Keeping the list, its normalization and its labels here — rather than
/// as string literals across the XAML and <c>App</c> — is the same idiom
/// <see cref="VolumeModes"/> and <see cref="SettingsSections"/> already use, and it is what makes
/// the binding testable without a shell.
///
/// One vocabulary serves every button: the left and middle buttons differ only in which of these
/// they default to, so adding an action makes it available to both at once.</summary>
public static class TrayActions
{
    /// <summary>Show the OSD as an interactive slider — the historical (and default) left-click
    /// behaviour, and the closest match to what the Windows volume icon does.</summary>
    public const string VolumeBar = "volume-bar";

    public const string Settings = "settings";
    public const string Equalizer = "equalizer";
    public const string Mute = "mute";

    /// <summary>A real binding, not an unset one: a user who wants the icon inert on click picks
    /// it deliberately, so it normalizes to itself rather than to a fallback.</summary>
    public const string None = "none";

    /// <summary>Every action, in the order the Settings combos list them.</summary>
    public static readonly IReadOnlyList<string> All =
        [VolumeBar, Settings, Equalizer, Mute, None];

    public static bool IsAction(string? action) => action is not null && All.Contains(action);

    /// <summary>Anything this build does not know — a value from a newer version, a hand-edited
    /// file, a missing field — falls back to what the CALLER considers sane for that button,
    /// because the left button's sane default (the volume bar) is not the middle button's
    /// (mute). Unlike <see cref="EqEditorModes.Normalize"/> there is no "never chosen" state to
    /// preserve here: a button is always bound to something.</summary>
    public static string Normalize(string? action, string fallback) =>
        IsAction(action) ? action! : fallback;

    /// <summary>The label the Settings combo shows. Lives here so an action added later cannot
    /// ship with its raw persisted name showing in the UI — the same reason
    /// <see cref="HudWidgetTypes.DisplayName"/> exists.</summary>
    public static string DisplayName(string action) => action switch
    {
        VolumeBar => "Open the volume slider",
        Settings => "Open Settings",
        Equalizer => "Open the equalizer",
        Mute => "Mute / unmute",
        None => "Do nothing",
        _ => action,
    };
}
