using System.Windows;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>A dev build has to be recognisable at a glance, so it says so in two places outside the
/// tray glyph (TrayGlyphTests owns the dot): the tray tooltip and the About card. A release build
/// must show neither — a stray "(dev)" on a shipped copy would make users think they installed the
/// wrong thing.
///
/// The About tests build the real <see cref="SettingsWindow"/> with the same arguments App passes
/// (fresh-install settings, no Equalizer APO reading) and drive <c>ShowBuild</c> with explicit
/// builds, since the test host's own <see cref="BuildInfo.Current"/> is whatever this run happens
/// to be.</summary>
[Collection("Wpf")]
public class DevBuildMarkerTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public DevBuildMarkerTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static readonly BuildInfo Dev = new(
        IsDev: true, Version: "2.4.0", Commit: "845c871d2a0e4f1b9c3d5e6f7a8b9c0d1e2f3a4b",
        Branch: "feat/themeable-notice-devbuild", BuiltAt: new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero));

    private static readonly BuildInfo Release = new(
        IsDev: false, Version: "2.4.0", Commit: "845c871d2a0e4f1b9c3d5e6f7a8b9c0d1e2f3a4b", Branch: null, BuiltAt: null);

    // ---- tooltip ----------------------------------------------------------------------------

    [Fact]
    public void Tooltip_gets_the_suffix_only_on_dev()
    {
        string suffix = Loc.T("app.tray.tooltip.dev-suffix");
        string dev = TrayIcon.Tooltip("AorinEQ: 40%", dev: true);
        string release = TrayIcon.Tooltip("AorinEQ: 40%", dev: false);
        _out.WriteLine($"suffix '{suffix}': dev '{dev}', release '{release}'");

        Assert.Equal("AorinEQ: 40%" + suffix, dev);
        Assert.Equal("AorinEQ: 40%", release);
    }

    /// <summary>NotifyIcon.Text throws past 127 characters, so a long tooltip (a long device name,
    /// a wordy translation) must be cut — and it is the base that gives way, because the suffix is
    /// the one part that must never be lost on a dev build. A 120-character base still fits whole;
    /// one past the room left beside the suffix loses exactly its overflow.</summary>
    [Fact]
    public void Tooltip_truncates_the_base_never_the_suffix()
    {
        string suffix = Loc.T("app.tray.tooltip.dev-suffix");
        int room = 127 - suffix.Length;
        string fits = TrayIcon.Tooltip(new string('x', 120), dev: true);
        string atRoom = TrayIcon.Tooltip(new string('y', room), dev: true);
        string over = TrayIcon.Tooltip(new string('z', 200), dev: true);
        _out.WriteLine($"suffix {suffix.Length} chars, room {room}: 120 -> {fits.Length}, {room} -> {atRoom.Length}, 200 -> {over.Length}");
        _out.WriteLine($"200-char base -> '{over}'");

        Assert.True(fits.Length <= 127, $"tooltip is {fits.Length} chars; NotifyIcon.Text throws past 127");
        Assert.Equal(new string('x', 120) + suffix, fits);
        Assert.Equal(new string('y', room) + suffix, atRoom);
        Assert.Equal(new string('z', room) + suffix, over);
        Assert.Equal(127, over.Length);
    }

    // ---- About ------------------------------------------------------------------------------

    /// <summary>Builds the real Settings window, hands it to <paramref name="body"/>, and hides it
    /// afterwards (SettingsWindow cancels Close by design — App owns its lifetime).</summary>
    private T With<T>(Func<SettingsWindow, T> body) =>
        _sta.Run(() =>
        {
            // Resolved from App.xaml in the app; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var window = new SettingsWindow(autostartEnabled: false, runAsAdmin: false, isElevated: false,
                version: "2.4.0", Settings.Default, health: null);
            try { return body(window); }
            finally { window.Hide(); }
        });

    [Fact]
    public void About_shows_the_badge_only_on_dev()
    {
        var (devBadge, devDetails, devBadgeText, devText) = With(w =>
        {
            w.ShowBuild(Dev);
            return (w.DevBuildBadge.Visibility, w.DevBuildDetails.Visibility, w.DevBuildBadgeText.Text, w.DevBuildDetails.Text);
        });
        _out.WriteLine($"dev: badge {devBadge} '{devBadgeText}', details {devDetails} '{devText}'");

        Assert.Equal(Visibility.Visible, devBadge);
        Assert.Equal(Visibility.Visible, devDetails);
        Assert.Equal(Loc.T("settings.about.dev-build"), devBadgeText);
        Assert.Contains(Dev.ShortCommit!, devText);
        Assert.Contains(Dev.Branch!, devText);
        Assert.DoesNotContain(Dev.Commit!, devText);   // the short form, not the full SHA

        var (relBadge, relDetails) = With(w =>
        {
            w.ShowBuild(Dev);       // a window that was showing a dev build...
            w.ShowBuild(Release);   // ...and is then told it is a release hides both again
            return (w.DevBuildBadge.Visibility, w.DevBuildDetails.Visibility);
        });
        _out.WriteLine($"release: badge {relBadge}, details {relDetails}");

        Assert.Equal(Visibility.Collapsed, relBadge);
        Assert.Equal(Visibility.Collapsed, relDetails);
    }

    /// <summary>A dev build with nothing known about it — no commit, no branch, no build time —
    /// still says what it is, with "unknown" in each gap rather than empty slots.</summary>
    [Fact]
    public void About_fills_unknown_details_with_the_unknown_word()
    {
        var bare = new BuildInfo(IsDev: true, Version: "0.0.0", Commit: null, Branch: null, BuiltAt: null);
        string text = With(w =>
        {
            w.ShowBuild(bare);
            return w.DevBuildDetails.Text;
        });
        string unknown = Loc.T("settings.about.unknown");
        _out.WriteLine($"bare dev build details: '{text}'");

        Assert.Equal(Loc.T("settings.about.dev-build-details", unknown, unknown, unknown), text);
    }

    /// <summary>The badge and details are set from code, so nothing repaints them on a language
    /// switch unless the window does it itself.</summary>
    [Fact]
    public void About_follows_a_language_change()
    {
        string previous = Loc.Language;
        try
        {
            var (badge, details) = With(w =>
            {
                Loc.Language = Languages.En;
                w.ShowBuild(Dev);
                Loc.Language = Languages.Ja;   // nothing but the language change touches the card now
                // The repaint is queued on the window's dispatcher; a lower-priority no-op returns
                // only once everything queued ahead of it has run.
                w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                return (w.DevBuildBadgeText.Text, w.DevBuildDetails.Text);
            });
            _out.WriteLine($"after switching to ja: badge '{badge}', details '{details}'");

            Assert.Equal(Loc.T("settings.about.dev-build"), badge);
            Assert.StartsWith(Loc.T("settings.about.dev-build-details").Split('{')[0], details);
            Assert.Contains(Dev.ShortCommit!, details);
        }
        finally
        {
            Loc.Language = previous;
        }
    }
}
