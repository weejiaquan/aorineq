namespace AorinEQ.Core;

/// <summary>When the OSD shows its AirPlay bar.
///
/// Spelled once here, the way <see cref="VolumeModes"/> and <see cref="SettingsSections"/> already
/// are, because the value is persisted and read by two different OSD windows - a skinned one and a
/// Fluent one - which must never disagree about whether AirPlay is on screen.</summary>
public static class AirPlayBarVisibility
{
    /// <summary>Never draw it. For someone who streams from the tray and wants the OSD to stay the
    /// one-line thing it has always been.</summary>
    public const string Never = "never";

    /// <summary>Only once there is a session. The default: a bar naming a receiver nobody is
    /// listening through is clutter, and the OSD is liked precisely because it says one thing and
    /// goes away.</summary>
    public const string Connected = "connected";

    /// <summary>Whenever AirPlay is switched on, connected or not. For someone who wants the
    /// receiver one click away - the bar is the thing they connect WITH, so it has to be there
    /// before there is anything to disconnect from.</summary>
    public const string Enabled = "enabled";

    public static readonly IReadOnlyList<string> All = [Never, Connected, Enabled];

    /// <summary>Anything unrecognised - including a hand-edited settings file, a value from a
    /// newer build, and the wrong case - reads as <see cref="Connected"/>. Falling back to the
    /// default rather than to Never matters: a typo should not silently remove a feature the user
    /// can no longer find the switch for.</summary>
    public static string Normalize(string? value) => All.Contains(value) ? value! : Connected;
}

/// <summary>What tapping the AirPlay strip's power button would do right now.</summary>
public enum AirPlayPower
{
    /// <summary>Nothing to connect to. No receiver has been chosen, so the button has no target -
    /// the strip falls back to opening the device list, which is where a first choice is made.</summary>
    Unavailable,

    /// <summary>Start a session with the chosen receiver.</summary>
    Connect,

    /// <summary>End the session we hold.</summary>
    Disconnect,
}

/// <summary>Everything the OSD needs to draw one AirPlay bar, and nothing about how it is drawn.
///
/// The decision lives in Core rather than in either OSD window because both of them make it, and
/// because "should this be on screen" is exactly the kind of rule that drifts when it is written
/// twice. The windows ask this type and render the answer.</summary>
/// <param name="Visible">Whether the bar is drawn at all.</param>
/// <param name="DeviceName">The chosen receiver's name, or empty when none has been chosen.</param>
/// <param name="IsConnected">A session exists - the receiver is ours, streaming or in standby.</param>
/// <param name="IsStreaming">Audio is actually going out.</param>
/// <param name="VolumePercent">The RECEIVER's own level, clamped to something drawable.</param>
public sealed record AirPlayBarState(
    bool Visible, string DeviceName, bool IsConnected, bool IsStreaming, int VolumePercent)
{
    /// <summary>Whether a receiver has been chosen at all. The bar can be visible without one -
    /// that is how <see cref="AirPlayBarVisibility.Enabled"/> lets someone pick their first
    /// device - so the two questions are not the same one.</summary>
    public bool HasDevice => DeviceName.Length > 0;

    /// <summary>What the strip's power button would do if tapped, and whether it should be drawn
    /// as usable at all.
    ///
    /// Here rather than in the view because both OSDs draw that button and both act on it, and
    /// because "connected" and "a receiver has been chosen" are two different facts that the one
    /// control has to combine - the same pair that <see cref="AirPlayMenuModel"/> combines for the
    /// menu, and they must not disagree.</summary>
    public AirPlayPower Power =>
        IsConnected ? AirPlayPower.Disconnect
        : HasDevice ? AirPlayPower.Connect
        : AirPlayPower.Unavailable;

    /// <summary>Nothing on screen. One instance so the windows can compare against it.</summary>
    public static readonly AirPlayBarState Hidden = new(false, "", false, false, 0);

    /// <summary>Reads the setting and the live session into the bar's state.
    ///
    /// <paramref name="connected"/> and <paramref name="streaming"/> come from the session rather
    /// than from settings, because settings record what the user ASKED for and the bar has to show
    /// what is actually happening - a receiver that has gone off the network is still named in the
    /// settings file.</summary>
    public static AirPlayBarState From(AirPlaySetting? setting, bool connected, bool streaming)
    {
        var s = setting ?? AirPlaySetting.Default;

        // AirPlay switched off beats every visibility choice. Otherwise turning the feature off
        // would leave its bar sitting on the OSD with nothing behind it.
        if (!s.Enabled) return Hidden;

        bool visible = AirPlayBarVisibility.Normalize(s.BarVisibility) switch
        {
            AirPlayBarVisibility.Never => false,
            AirPlayBarVisibility.Enabled => true,
            _ => connected,
        };

        if (!visible) return Hidden;

        return new AirPlayBarState(
            Visible: true,
            DeviceName: s.DeviceName ?? "",
            IsConnected: connected,
            IsStreaming: streaming,
            // Clamped here rather than trusted: this number is persisted, and a hand-edited file
            // must not make the fill overrun the bar it is drawn inside or invert it.
            VolumePercent: Math.Clamp(s.VolumePercent, 0, 100));
    }
}

/// <summary>Which items the AirPlay bar's dropdown offers.
///
/// In Core, and away from the menu that renders it, for the same reason
/// <see cref="AirPlayBarState"/> is: both OSD windows open this menu, and the rule got one
/// condition wrong in a way no screenshot could show. A menu opened before anything had been
/// discovered offered Rescan and nothing else - Connect required the chosen receiver to be in the
/// discovered list, and nothing discovers until the user asks, which is what the menu is for.
///
/// The list being empty is a fact about DISCOVERY. Having chosen a receiver is a fact about
/// SETTINGS. Connect belongs to the second, so it must not be gated on the first.</summary>
/// <param name="NoneFound">Say so, in place of the rows. Ordinary before the first scan.</param>
/// <param name="Disconnect">There is a session to end.</param>
/// <param name="Connect">A receiver has been chosen and is not the one we hold, so connecting to
/// it is a thing the user can ask for - whether or not it has been discovered yet.</param>
public sealed record AirPlayMenuModel(bool NoneFound, bool Disconnect, bool Connect)
{
    /// <param name="deviceCount">How many receivers discovery has found.</param>
    /// <param name="currentId">The receiver this app holds a session with, if any.</param>
    /// <param name="chosenId">The receiver named in settings, which is not the same thing.</param>
    public static AirPlayMenuModel For(int deviceCount, string? currentId, string? chosenId)
    {
        bool connected = !string.IsNullOrEmpty(currentId);
        return new AirPlayMenuModel(
            NoneFound: deviceCount == 0,
            Disconnect: connected,
            // Never both: ending the session you have and starting the one you chose are the same
            // row on the menu, and offering the two together says nothing about which you are on.
            Connect: !connected && !string.IsNullOrEmpty(chosenId));
    }
}
