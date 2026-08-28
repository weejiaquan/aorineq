namespace AorinEQ.Core;

/// <summary>Every topic AorinEQ can explain.

///
/// Structure only: each entry names the string keys its words live under, so this file does not
/// change when the wording does, and adding a language does not touch it at all.
///
/// The list is not hand-maintained against the windows - HelpXamlCoverageTests reads the real XAML
/// and fails the build if an interactive control has neither a topic here nor an entry on its
/// exemption list. That is what keeps "the app explains itself" true a year from now rather than
/// only in the release that shipped it.</summary>
public static class HelpCatalogue
{
    public static readonly IReadOnlyList<HelpTopic> Topics =
    [
        // ---- volume ----
        new("settings.system-mode", SettingsSections.Volume,
            "settings.system-mode.title", "settings.system-mode.subtitle", "#volume-modes"),
        new("settings.eapo-mode", SettingsSections.Volume,
            "settings.eapo-mode.title", "settings.eapo-mode.subtitle", "#volume-modes"),
        new("settings.step", SettingsSections.Volume,
            "settings.step.title", "settings.step.subtitle", "#volume-modes"),
        new("settings.device-volume", SettingsSections.Volume,
            "settings.device-volume.title", null, "#volume-modes"),
        // ---- osd ----
        new("settings.style", SettingsSections.Osd,
            "settings.style.title", "settings.style.subtitle", "#the-osd"),
        new("settings.anchor-top-left", SettingsSections.Osd,
            "settings.anchor-top-left.title", "settings.anchor-top-left.subtitle", "#the-osd"),
        new("settings.offset-x", SettingsSections.Osd,
            "settings.offset-x.title", "settings.offset-x.subtitle", "#the-osd"),
        new("settings.hide-delay", SettingsSections.Osd,
            "settings.hide-delay.title", null, "#the-osd"),
        new("settings.animation-check", SettingsSections.Osd,
            "settings.animation-check.title", "settings.animation-check.subtitle", "#the-osd"),
        new("settings.animation-duration", SettingsSections.Osd,
            "settings.animation-duration.title", null, "#the-osd"),
        // ---- skins ----
        new("settings.skin-credit", SettingsSections.Skins,
            "settings.skin-credit.title", "settings.skin-credit.subtitle", "#skins"),
        new("settings.skin-designer", SettingsSections.Skins,
            "settings.skin-designer.title", "settings.skin-designer.subtitle", "#skins"),
        // ---- equalizer ----
        new("settings.open-equalizer", SettingsSections.Equalizer,
            "settings.open-equalizer.title", "settings.open-equalizer.subtitle", "#equalizer"),
        // ---- airplay ----
        new("settings.air-play-status", SettingsSections.AirPlay,
            "settings.air-play-status.title", "settings.air-play-status.subtitle", "#airplay"),
        new("settings.air-play-source", SettingsSections.AirPlay,
            "settings.air-play-source.title", "settings.air-play-source.subtitle", "#airplay"),
        new("settings.air-play-mode", SettingsSections.AirPlay,
            "settings.air-play-mode.title", "settings.air-play-mode.subtitle", "#airplay"),
        new("settings.air-play-volume", SettingsSections.AirPlay,
            "settings.air-play-volume.title", "settings.air-play-volume.subtitle", "#airplay"),
        new("settings.air-play-retarget-note", SettingsSections.AirPlay,
            "settings.air-play-retarget-note.title", "settings.air-play-retarget-note.subtitle", "#airplay"),
        new("settings.air-play-mute-local", SettingsSections.AirPlay,
            "settings.air-play-mute-local.title", "settings.air-play-mute-local.subtitle", "#airplay"),
        new("settings.air-play-dither", SettingsSections.AirPlay,
            "settings.air-play-dither.title", "settings.air-play-dither.subtitle", "#airplay"),
        new("settings.air-play-bar", SettingsSections.AirPlay,
            "settings.air-play-bar.title", "settings.air-play-bar.subtitle", "#airplay"),
        new("settings.air-play-idle-seconds", SettingsSections.AirPlay,
            "settings.air-play-idle-seconds.title", "settings.air-play-idle-seconds.subtitle", "#airplay"),
        // ---- hud ----
        new("settings.hud-edit-switch", SettingsSections.Hud,
            "settings.hud-edit-switch.title", "settings.hud-edit-switch.subtitle", "#hud-widgets"),
        new("settings.add-spectrum", SettingsSections.Hud,
            "settings.add-spectrum.title", "settings.add-spectrum.subtitle", "#hud-widgets"),
        new("settings.hud-fullscreen-switch", SettingsSections.Hud,
            "settings.hud-fullscreen-switch.title", "settings.hud-fullscreen-switch.subtitle", "#hud-widgets"),
        new("settings.hud-only-playing-switch", SettingsSections.Hud,
            "settings.hud-only-playing-switch.title", "settings.hud-only-playing-switch.subtitle", "#hud-widgets"),
        new("settings.hud-fps", SettingsSections.Hud,
            "settings.hud-fps.title", "settings.hud-fps.subtitle", "#hud-widgets"),
        // ---- general (AorinEQ configuring itself) ----
        new("settings.tray-left-click", SettingsSections.General,
            "settings.tray-left-click.title", "settings.tray-left-click.subtitle", "#volume-modes"),
        new("settings.tray-middle-click", SettingsSections.General,
            "settings.tray-middle-click.title", "settings.tray-middle-click.subtitle", "#volume-modes"),
        new("settings.tray-scroll", SettingsSections.General,
            "settings.tray-scroll.title", "settings.tray-scroll.subtitle", "#volume-modes"),
        new("settings.scroll-inverted", SettingsSections.General,
            "settings.scroll-inverted.title", "settings.scroll-inverted.subtitle", "#volume-modes"),
        new("settings.autostart", SettingsSections.General,
            "settings.autostart.title", "settings.autostart.subtitle", "#volume-modes"),
        new("settings.elevation-state", SettingsSections.General,
            "settings.elevation-state.title", "settings.elevation-state.subtitle", "#volume-modes"),
        new("settings.protocol-links", SettingsSections.General,
            "settings.protocol-links.title", "settings.protocol-links.subtitle", "#skins"),
        // ---- updates ----
        new("settings.auto-update", SettingsSections.Updates,
            "settings.auto-update.title", "settings.auto-update.subtitle", "#auto-update"),
        new("settings.update-status", SettingsSections.Updates,
            "settings.update-status.title", null, "#auto-update"),

        // ---- the HUD widget kinds, reached from the tray's "Add widget" submenu ----
        // That menu was four bare nouns before this: Spectrum, Levels, EQ curve, Volume. Nothing
        // there said what any of them showed.
        new("hud.widget.spectrum", SettingsSections.Hud, "hud.widget.spectrum.name", null, "#hud-widgets"),
        new("hud.widget.levels", SettingsSections.Hud, "hud.widget.levels.name", null, "#hud-widgets"),
        new("hud.widget.eqcurve", SettingsSections.Hud, "hud.widget.eqcurve.name", null, "#hud-widgets"),
        new("hud.widget.volume", SettingsSections.Hud, "hud.widget.volume.name", null, "#hud-widgets"),
    ];

    private static readonly Dictionary<string, HelpTopic> ByKey =
        Topics.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The topic with this key, or null. Null rather than throwing because the caller -
    /// the XAML decorator - produces a far better message with the control in hand.</summary>
    public static HelpTopic? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Every topic on one window or settings page, in declaration order. This is what a
    /// per-window help panel renders, so a topic added here appears there without either window
    /// being touched.</summary>
    public static IReadOnlyList<HelpTopic> ForSurface(string surface) =>
        [.. Topics.Where(t => t.Surface == surface)];

    /// <summary>Topics matching <paramref name="query"/>, best first.
    ///
    /// Matches the ACTIVE language and English, always both. English is not a fallback here, it is
    /// a deliberate second index: almost all audio documentation online is in English, so a Korean
    /// user who has just read a forum post about "pre-ringing" types exactly that. Searching only
    /// the active table would find nothing, and finding nothing reads as the feature not existing.
    ///
    /// An empty query returns nothing rather than everything - a search box that lists all sixty
    /// topics the moment it is focused is noise, not a result.</summary>
    public static IReadOnlyList<HelpTopic> Search(string query)
    {
        var needle = query?.Trim();
        if (string.IsNullOrEmpty(needle)) return [];

        return
        [
            .. Topics
                .Select(topic => (topic, score: Score(topic, needle)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.topic.Key, StringComparer.Ordinal)
                .Select(x => x.topic)
        ];
    }

    private static int Score(HelpTopic topic, string needle)
    {
        var best = ScoreIn(Loc.Raw(Languages.En), topic, needle);

        if (Loc.Language != Languages.En)
            best = Math.Max(best, ScoreIn(Loc.Raw(Loc.Language), topic, needle));

        return best;
    }

    /// <summary>Where a match landed decides how good it is. A title match is what the user meant;
    /// a body match is a topic that merely mentions the word.</summary>
    private static int ScoreIn(IReadOnlyDictionary<string, string> table, HelpTopic topic, string needle)
    {
        var title = table.GetValueOrDefault(topic.TitleKey, "");
        var summary = topic.SummaryKey is null ? "" : table.GetValueOrDefault(topic.SummaryKey, "");
        var body = table.GetValueOrDefault($"help.{topic.Key}.body", "");

        if (title.Equals(needle, StringComparison.OrdinalIgnoreCase)) return 100;
        if (title.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 80;
        if (title.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 60;
        if (summary.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 40;
        if (body.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 20;

        return 0;
    }
}
