using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The Skins page and the HUD page's add row, on the real Settings window.
///
/// The skins are the real shipped ones in the real skins folder - topped up first, exactly as App
/// does at startup - because the page scans that folder and nothing else. A made-up skin in a
/// temp folder could not appear in the picker.</summary>
[Collection("Wpf")]
public class SettingsSkinsPageTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public SettingsSkinsPageTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        DefaultSkins.EnsureInstalled(ApoPaths.GetSkinsRoot());
    }

    private T With<T>(Settings settings, Func<SettingsWindow, T> body) =>
        _sta.Run(() =>
        {
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var window = new SettingsWindow(autostartEnabled: false, runAsAdmin: false, isElevated: false,
                version: "0.0.0", settings, health: null);
            try { return body(window); }
            finally { window.Hide(); }
        });

    private static ComboBoxItem ItemTagged(System.Windows.Controls.ComboBox combo, string tag) =>
        combo.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == tag);

    private static string? SelectedTag(System.Windows.Controls.ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>The picker used to be disabled until the style was set to Custom skin on the OSD
    /// page, whose help sent people to the Skins page first. Picking a skin now switches the style
    /// and raises ONE change carrying both.</summary>
    [Fact]
    public void Picking_a_skin_while_another_style_is_set_switches_the_style_to_custom_skin()
    {
        var settings = Settings.Default with { OsdStyle = OsdStyles.DarkPill, SkinName = SkinArt.Default };
        var r = With(settings, w =>
        {
            bool enabledBefore = w.SkinCombo.IsEnabled;
            var raised = new List<OsdSettings>();
            w.OsdSettingsChanged += raised.Add;
            w.SkinCombo.SelectedItem = ItemTagged(w.SkinCombo, "windows-11");
            return (enabledBefore, Style: SelectedTag(w.StyleCombo), raised);
        });
        _out.WriteLine($"picker enabled before={r.enabledBefore}; style after={r.Style}; raised "
                     + string.Join(", ", r.raised.Select(o => $"{o.Style}/{o.SkinName}")));

        Assert.True(r.enabledBefore, "the skin picker must be usable whatever the style is");
        Assert.Equal(OsdStyles.Skin, r.Style);
        var change = Assert.Single(r.raised);
        Assert.Equal(OsdStyles.Skin, change.Style);
        Assert.Equal("windows-11", change.SkinName);
    }

    [Fact]
    public void Picking_a_skin_while_custom_skin_is_already_set_raises_one_change_and_keeps_the_style()
    {
        var settings = Settings.Default with { OsdStyle = OsdStyles.Skin, SkinName = SkinArt.Default };
        var r = With(settings, w =>
        {
            var raised = new List<OsdSettings>();
            w.OsdSettingsChanged += raised.Add;
            w.SkinCombo.SelectedItem = ItemTagged(w.SkinCombo, "windows-xp");
            return (Style: SelectedTag(w.StyleCombo), raised);
        });
        _out.WriteLine($"style={r.Style}; raised " + string.Join(", ", r.raised.Select(o => $"{o.Style}/{o.SkinName}")));

        Assert.Equal(OsdStyles.Skin, r.Style);
        Assert.Equal("windows-xp", Assert.Single(r.raised).SkinName);
    }

    /// <summary>Opening Settings populates the picker; that must not count as picking, or merely
    /// opening the window would switch a Dark pill user to Custom skin.</summary>
    [Fact]
    public void Opening_settings_does_not_switch_the_style()
    {
        var settings = Settings.Default with { OsdStyle = OsdStyles.Fluent, SkinName = SkinArt.Default };
        var style = With(settings, w => SelectedTag(w.StyleCombo));
        _out.WriteLine($"style after opening: {style}");

        Assert.Equal(OsdStyles.Fluent, style);
    }

    /// <summary>The shipped skins carry an AirPlay strip and a device notice; the page says so and
    /// draws the skin with the OSD's own renderer.</summary>
    [Fact]
    public void The_selected_skin_shows_what_it_carries_and_a_live_preview()
    {
        var settings = Settings.Default with { OsdStyle = OsdStyles.Skin, SkinName = SkinArt.Default };
        var r = With(settings, w => (Features: w.SkinFeaturesText.Text, FeaturesShown: w.SkinFeaturesText.Visibility,
            Problem: w.SkinProblemText.Visibility, PreviewShown: w.SkinPreviewBox.Visibility,
            Preview: w.SkinPreviewBox.Child?.GetType().Name));
        _out.WriteLine($"features='{r.Features}' ({r.FeaturesShown}); problem={r.Problem}; preview={r.Preview} ({r.PreviewShown})");

        Assert.Equal(Visibility.Visible, r.FeaturesShown);
        Assert.Contains(Loc.T("settings.skin-features.airplay"), r.Features);
        Assert.Contains(Loc.T("settings.skin-features.notice"), r.Features);
        Assert.Equal(Visibility.Collapsed, r.Problem);
        Assert.Equal(Visibility.Visible, r.PreviewShown);
        Assert.Equal(nameof(SkinView), r.Preview);
    }

    /// <summary>A broken skin's reason lived only in the tooltip of a DISABLED item, which WPF
    /// does not show unless told to. Every item must be told, since any of them can break.</summary>
    [Fact]
    public void Every_skin_item_shows_its_tooltip_even_when_disabled()
    {
        var settings = Settings.Default with { OsdStyle = OsdStyles.Skin, SkinName = SkinArt.Default };
        var items = With(settings, w => w.SkinCombo.Items.Cast<ComboBoxItem>()
            .Select(i => (Tag: (string)i.Tag, ShowOnDisabled: ToolTipService.GetShowOnDisabled(i), i.IsEnabled)).ToList());
        foreach (var i in items) _out.WriteLine($"{i.Tag}: enabled={i.IsEnabled} showOnDisabled={i.ShowOnDisabled}");

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(i.ShowOnDisabled, $"{i.Tag} would hide its error when disabled"));
    }

    /// <summary>Past <see cref="HudLayout.MaxWidgets"/> an add is a silent no-op, so the buttons are
    /// disabled and the page says why - and re-enabled the moment there is room again.</summary>
    [Fact]
    public void The_add_widget_buttons_disable_at_the_cap_and_say_why()
    {
        static HudLayout Holding(int count) => new()
        {
            Widgets = Enumerable.Range(0, count).Select(_ => HudWidget.Create(HudWidgetTypes.Spectrum)).ToList(),
        };
        var r = With(Settings.Default, w =>
        {
            w.ApplyHud(Holding(HudLayout.MaxWidgets));
            var full = (w.AddSpectrumButton.IsEnabled, w.AddVolumeButton.IsEnabled, w.HudFullText.Visibility, w.HudFullText.Text);
            w.ApplyHud(Holding(HudLayout.MaxWidgets - 1));
            var room = (w.AddSpectrumButton.IsEnabled, w.AddVolumeButton.IsEnabled, w.HudFullText.Visibility);
            return (full, room);
        });
        _out.WriteLine($"at {HudLayout.MaxWidgets}: {r.full}; at {HudLayout.MaxWidgets - 1}: {r.room}");

        Assert.False(r.full.Item1);
        Assert.False(r.full.Item2);
        Assert.Equal(Visibility.Visible, r.full.Item3);
        Assert.Contains(HudLayout.MaxWidgets.ToString(), r.full.Item4);
        Assert.True(r.room.Item1);
        Assert.True(r.room.Item2);
        Assert.Equal(Visibility.Collapsed, r.room.Item3);
    }
}
