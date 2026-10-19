using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The AirPlay page's VB-Cable rows, on the real Settings window: which cards show in each
/// cable state, how AirPlay only locks the Source combo on the cable and hides "Mute this PC", and
/// that the page never rewrites the user's stored source while doing it. The decisions are the
/// window's own methods, so these tests drive exactly those; App's half (reading the machine,
/// running the elevated helper) is covered by the Core tests and Task 14's live check.
///
/// A "cable" here is a real active endpoint standing in for CABLE Input: the page only ever sees an
/// endpoint id, and the Source combo only lists real endpoints, so a made-up id could not be shown.</summary>
[Collection("Wpf")]
public class AirPlayCablePageTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AirPlayCablePageTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>Builds the real Settings window from <paramref name="settings"/>, hands it to
    /// <paramref name="body"/>, and hides it afterwards (SettingsWindow cancels Close by design).</summary>
    private T With<T>(Settings settings, Func<SettingsWindow, T> body) =>
        _sta.Run(() =>
        {
            // Resolved from App.xaml in the app; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var window = new SettingsWindow(autostartEnabled: false, runAsAdmin: false, isElevated: false,
                version: "0.0.0", settings, health: null);
            try { return body(window); }
            finally { window.Hide(); }
        });

    private static Settings WithAirPlay(AirPlaySetting airPlay) => Settings.Default with { AirPlay = airPlay };

    private static string? SelectedSource(SettingsWindow w) =>
        (w.AirPlaySourceCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>What a mouse click raises; the XAML wires these controls by Click=.</summary>
    private static void Click(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>A real active endpoint to stand in for CABLE Input.</summary>
    private static string StandInCable() => AudioEndpoint.GetRenderEndpoints().First().Id;

    // ---------------------------------------------------------------- AirPlay only and EQ rows

    [Fact]
    public void Without_a_cable_the_airplay_only_and_eq_rows_stay_hidden_and_nothing_is_locked()
    {
        // Switched on in settings, but no cable to route through: it must not lock anything.
        var r = With(WithAirPlay(AirPlaySetting.Default with { AirPlayOnly = true }), w =>
        {
            w.SetVirtualCable(VirtualCableState.Missing, null);
            return (Only: w.AirPlayOnlyCard.Visibility, Eq: w.AirPlayEqStreamCard.Visibility,
                Mute: w.AirPlayMuteLocalCard.Visibility, SourceEnabled: w.AirPlaySourceCombo.IsEnabled);
        });
        _out.WriteLine($"no cable: only={r.Only} eq={r.Eq} mute={r.Mute} source enabled={r.SourceEnabled}");
        Assert.Equal(Visibility.Collapsed, r.Only);
        Assert.Equal(Visibility.Collapsed, r.Eq);
        Assert.Equal(Visibility.Visible, r.Mute);
        Assert.True(r.SourceEnabled);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_present_cable_shows_both_rows_and_with_AirPlay_only_off_source_and_mute_stay_the_users()
    {
        var cable = StandInCable();
        var r = With(WithAirPlay(AirPlaySetting.Default), w =>
        {
            w.SetVirtualCable(VirtualCableState.Present, cable);
            return (Only: w.AirPlayOnlyCard.Visibility, Eq: w.AirPlayEqStreamCard.Visibility,
                Mute: w.AirPlayMuteLocalCard.Visibility, SourceEnabled: w.AirPlaySourceCombo.IsEnabled,
                Source: SelectedSource(w), Toggle: w.AirPlayOnlyBox.IsChecked);
        });
        _out.WriteLine($"cable {cable}, AirPlay only off: {r}");
        Assert.Equal(Visibility.Visible, r.Only);
        Assert.Equal(Visibility.Visible, r.Eq);
        Assert.Equal(Visibility.Visible, r.Mute);
        Assert.True(r.SourceEnabled);
        Assert.Equal("", r.Source);     // "Default playback device", as stored
        Assert.False(r.Toggle);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void AirPlay_only_locks_the_source_on_the_cable_hides_mute_and_never_rewrites_the_stored_source()
    {
        var cable = StandInCable();
        var r = With(WithAirPlay(AirPlaySetting.Default with { AirPlayOnly = true, SourceEndpointId = "" }), w =>
        {
            var raised = new List<AirPlaySetting>();
            w.AirPlaySettingsChanged += raised.Add;
            w.SetVirtualCable(VirtualCableState.Present, cable);
            var locked = (Source: SelectedSource(w), Enabled: w.AirPlaySourceCombo.IsEnabled, Mute: w.AirPlayMuteLocalCard.Visibility);
            w.SetVirtualCable(VirtualCableState.Missing, null); // the cable goes away
            var released = (Source: SelectedSource(w), Enabled: w.AirPlaySourceCombo.IsEnabled, Mute: w.AirPlayMuteLocalCard.Visibility);
            return (Locked: locked, Released: released, Raised: raised.Count);
        });
        _out.WriteLine($"locked {r.Locked}; after the cable left {r.Released}; settings raised {r.Raised} times");
        Assert.Equal((cable, false, Visibility.Collapsed), (r.Locked.Source, r.Locked.Enabled, r.Locked.Mute));
        Assert.Equal(("", true, Visibility.Visible), (r.Released.Source, r.Released.Enabled, r.Released.Mute));
        Assert.Equal(0, r.Raised); // showing the cable is not the user choosing it
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_cable_that_appeared_after_the_window_was_built_is_listed_and_selected()
    {
        var cable = StandInCable();
        var r = With(WithAirPlay(AirPlaySetting.Default with { AirPlayOnly = true }), w =>
        {
            // The window was built before the cable existed, so its Source list lacks it.
            w.AirPlaySourceCombo.Items.Clear();
            w.SetVirtualCable(VirtualCableState.Present, cable);
            var tags = w.AirPlaySourceCombo.Items.OfType<ComboBoxItem>().Select(i => i.Tag as string).ToList();
            return (Tags: tags, Selected: SelectedSource(w));
        });
        _out.WriteLine($"refilled list: [{string.Join(", ", r.Tags)}]; selected {r.Selected}");
        Assert.Contains("", r.Tags);
        Assert.Contains(cable, r.Tags);
        Assert.Equal(cable, r.Selected);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Turning_AirPlay_only_on_raises_it_keeps_the_stored_source_and_locks_the_combo()
    {
        var cable = StandInCable();
        var r = With(WithAirPlay(AirPlaySetting.Default), w =>
        {
            var raised = new List<AirPlaySetting>();
            w.SetVirtualCable(VirtualCableState.Present, cable);
            w.AirPlaySettingsChanged += raised.Add;
            w.AirPlayOnlyBox.IsChecked = true;
            Click(w.AirPlayOnlyBox);
            return (Raised: raised.ToList(), Source: SelectedSource(w), Enabled: w.AirPlaySourceCombo.IsEnabled,
                Mute: w.AirPlayMuteLocalCard.Visibility);
        });
        _out.WriteLine($"raised {r.Raised.Count}: {string.Join(" | ", r.Raised)}; shown {r.Source}, enabled {r.Enabled}, mute {r.Mute}");
        var only = Assert.Single(r.Raised);
        Assert.True(only.AirPlayOnly);
        Assert.Equal("", only.SourceEndpointId); // the user's own choice survives
        Assert.Equal(cable, r.Source);
        Assert.False(r.Enabled);
        Assert.Equal(Visibility.Collapsed, r.Mute);
    }

    [Fact]
    public void The_eq_toggle_shows_the_machines_answer_and_edit_needs_it_on()
    {
        var r = With(Settings.Default, w =>
        {
            w.SetAirPlayEqStream(true);
            var on = (w.AirPlayEqStreamBox.IsChecked, w.AirPlayEqStreamBox.IsEnabled, w.AirPlayEqEditButton.IsEnabled);
            w.SetAirPlayEqStream(false);
            var off = (w.AirPlayEqStreamBox.IsChecked, w.AirPlayEqStreamBox.IsEnabled, w.AirPlayEqEditButton.IsEnabled);
            return (On: on, Off: off);
        });
        _out.WriteLine($"active: {r.On}; inactive: {r.Off}");
        Assert.Equal(((bool?)true, true, true), r.On);
        Assert.Equal(((bool?)false, true, false), r.Off);
    }

    [Fact]
    public void Flipping_the_eq_toggle_asks_app_once_and_waits_for_its_answer()
    {
        var r = With(Settings.Default, w =>
        {
            var asked = new List<bool>();
            w.AirPlayEqStreamToggled += asked.Add;
            w.AirPlayEqStreamBox.IsChecked = true;
            Click(w.AirPlayEqStreamBox);
            bool waiting = w.AirPlayEqStreamBox.IsEnabled;
            w.SetAirPlayEqStream(true); // App's answer, read from the machine
            return (Asked: asked.ToList(), EnabledWhileWaiting: waiting, EnabledAfter: w.AirPlayEqStreamBox.IsEnabled);
        });
        _out.WriteLine($"asked [{string.Join(", ", r.Asked)}]; enabled while waiting={r.EnabledWhileWaiting}, after={r.EnabledAfter}");
        Assert.Equal(new[] { true }, r.Asked);
        Assert.False(r.EnabledWhileWaiting); // a second click would only be a second UAC prompt
        Assert.True(r.EnabledAfter);
    }

    [Fact]
    public void The_cable_repairs_progress_and_verdict_show_on_the_eq_card()
    {
        var busy = Loc.T("app.repairing");
        var r = With(Settings.Default, w =>
        {
            var before = w.AirPlayEqStreamStatus.Visibility;
            w.SetAirPlayEqStreamStatus(busy);
            var shown = (w.AirPlayEqStreamStatus.Visibility, w.AirPlayEqStreamStatus.Text);
            w.SetAirPlayEqStreamStatus(null);
            return (Before: before, Shown: shown, After: w.AirPlayEqStreamStatus.Visibility);
        });
        _out.WriteLine($"before {r.Before}; shown {r.Shown}; cleared {r.After}");
        Assert.Equal(Visibility.Collapsed, r.Before);
        Assert.Equal((Visibility.Visible, busy), r.Shown);
        Assert.Equal(Visibility.Collapsed, r.After);
    }

    [Fact]
    public void What_App_tells_the_page_is_not_the_user_acting_so_it_raises_nothing()
    {
        var r = With(WithAirPlay(AirPlaySetting.Default with { AirPlayOnly = true }), w =>
        {
            var toggled = new List<bool>();
            var settings = new List<AirPlaySetting>();
            w.AirPlayEqStreamToggled += toggled.Add;
            w.AirPlaySettingsChanged += settings.Add;
            w.SetAirPlayEqStream(true);
            w.SetAirPlayEqStream(false);
            // A made-up id is enough here: nothing is selected by it, only hidden and shown.
            w.SetVirtualCable(VirtualCableState.Present, "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}");
            w.SetVirtualCable(VirtualCableState.Inactive, null);
            w.SetVirtualCable(VirtualCableState.Missing, null);
            return (Toggled: toggled.Count, Settings: settings.Count);
        });
        _out.WriteLine($"after programmatic pushes: toggled raised {r.Toggled}, settings raised {r.Settings}");
        Assert.Equal(0, r.Toggled);
        Assert.Equal(0, r.Settings);
    }

    // ---------------------------------------------------------------- App's repair routing

    /// <summary>The toggle keeps showing the user's request only while the CABLE'S OWN repair runs;
    /// while the default device's runs, it shows the machine, so a request refused because of that
    /// run snaps back at once instead of staying greyed out.</summary>
    [Theory]
    [InlineData(false, null, "{AAAA}", false)]                // nothing running
    [InlineData(true, null, "{AAAA}", false)]                 // the default device's repair
    [InlineData(true, "{BBBB}", "{AAAA}", false)]             // some other named endpoint
    [InlineData(true, "{AAAA}", "{AAAA}", true)]              // the cable's own repair
    [InlineData(true, "{aaaa}", "{AAAA}", true)]              // case is not identity
    [InlineData(false, "{AAAA}", "{AAAA}", false)]            // a stale guid after the run ended
    public void The_eq_toggle_holds_the_users_request_only_during_the_cables_own_repair(
        bool running, string? runningGuid, string cableGuid, bool expected)
    {
        bool holds = App.RepairRunningOn(running, runningGuid, cableGuid);
        _out.WriteLine($"running={running} run's endpoint={runningGuid ?? "(default)"} cable={cableGuid} -> holds={holds}");
        Assert.Equal(expected, holds);
    }

    /// <summary>A refused request (another repair holds the helper) is said on the surface that
    /// asked: the cable's on the EQ card, the default device's on the Volume page — never on the
    /// other one, which may be showing the run that holds it.</summary>
    [Fact]
    public void A_refusal_is_said_where_it_was_asked()
    {
        var refused = Loc.T("app.aorineq-is-already-changing-this-pc");
        var busy = Loc.T("app.repairing");
        var r = With(Settings.Default, w =>
        {
            // The default device's repair is running and reporting on the Volume page...
            App.ShowRepairStatus(w, null, busy, busy: true);
            // ...when the cable's toggle asks.
            App.ShowRepairStatus(w, "{AAAA}", refused, busy: false);
            var cableAsked = (Volume: w.EapoRepairStatusText.Text, Eq: w.AirPlayEqStreamStatus.Text,
                EqShown: w.AirPlayEqStreamStatus.Visibility, RepairButton: w.RepairEapoButton.IsEnabled);
            // And the other way round: the cable's repair is reporting on the EQ card when the
            // Volume page asks.
            App.ShowRepairStatus(w, "{AAAA}", busy, busy: true);
            App.ShowRepairStatus(w, null, refused, busy: false);
            var defaultAsked = (Volume: w.EapoRepairStatusText.Text, Eq: w.AirPlayEqStreamStatus.Text);
            return (CableAsked: cableAsked, DefaultAsked: defaultAsked);
        });
        _out.WriteLine($"cable asked during the default's run: {r.CableAsked}");
        _out.WriteLine($"default asked during the cable's run: {r.DefaultAsked}");
        Assert.Equal((busy, refused, Visibility.Visible, false), r.CableAsked); // the Volume page still shows its run
        Assert.Equal((refused, busy), r.DefaultAsked);                         // the EQ card still shows its run
    }
}
