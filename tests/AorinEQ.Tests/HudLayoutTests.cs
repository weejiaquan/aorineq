using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>hud.json — the HUD's layout record. Separate from settings.json on purpose: this file
/// is rewritten by dragging, and mixing it into the settings record would make every drag a
/// settings write. These tests use real files in real temp directories, like the rest of the
/// suite.</summary>
public class HudLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-hud-" + Guid.NewGuid().ToString("N"));

    public HudLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    // ---- defaults ----

    [Fact]
    public void A_missing_file_loads_the_documented_defaults()
    {
        var layout = HudLayout.Load(Path_("nope.json"));

        Assert.Empty(layout.Widgets);
        Assert.Equal(HudModes.Live, layout.Mode);
        // Both spec'd defaults: hide over fullscreen ON, show-only-while-playing OFF.
        Assert.True(layout.HideWhenFullscreen);
        Assert.False(layout.OnlyWhilePlaying);
        Assert.Equal(HudLayout.DefaultFps, layout.Fps);
    }

    [Fact]
    public void Unparseable_json_loads_defaults_rather_than_throwing()
    {
        var path = Path_("broken.json");
        File.WriteAllText(path, "{ this is not json");

        var layout = HudLayout.Load(path);

        Assert.Empty(layout.Widgets);
        Assert.Equal(HudModes.Live, layout.Mode);
    }

    // ---- round trip ----

    [Fact]
    public void A_full_layout_round_trips_through_the_file()
    {
        var path = Path_("hud.json");
        var layout = new HudLayout
        {
            Mode = HudModes.Edit,
            HideWhenFullscreen = false,
            OnlyWhilePlaying = true,
            Fps = 24,
            Widgets =
            [
                new HudWidget
                {
                    Id = "w1",
                    Type = HudWidgetTypes.Spectrum,
                    MonitorId = @"\\?\DISPLAY#GSM5B09#5&abc&0&UID1",
                    X = 100, Y = 200, Width = 320, Height = 120, Z = 3, Visible = true,
                    BandCount = 48, MinHz = 30, MaxHz = 16000, Smoothing = 0.4,
                    PeakHold = true, PeakDecayDbPerSecond = 30, Orientation = HudOrientations.RightToLeft,
                    BarGap = 3, Opacity = 0.75, ColorStart = "#FF00A0FF", ColorEnd = "#FFFF3060",
                },
                new HudWidget
                {
                    Id = "w2", Type = HudWidgetTypes.Volume, MonitorId = "",
                    X = 0, Y = 0, Width = 200, Height = 60, Z = 1, Visible = false, Scale = 1.5,
                },
            ],
        };

        layout.Save(path);
        var back = HudLayout.Load(path);

        Assert.Equal(HudModes.Edit, back.Mode);
        Assert.False(back.HideWhenFullscreen);
        Assert.True(back.OnlyWhilePlaying);
        Assert.Equal(24, back.Fps);
        Assert.Equal(2, back.Widgets.Count);

        var w1 = back.Widgets[0];
        Assert.Equal("w1", w1.Id);
        Assert.Equal(HudWidgetTypes.Spectrum, w1.Type);
        Assert.Equal(@"\\?\DISPLAY#GSM5B09#5&abc&0&UID1", w1.MonitorId);
        Assert.Equal(100, w1.X);
        Assert.Equal(200, w1.Y);
        Assert.Equal(320, w1.Width);
        Assert.Equal(120, w1.Height);
        Assert.Equal(3, w1.Z);
        Assert.True(w1.Visible);
        Assert.Equal(48, w1.BandCount);
        Assert.Equal(30, w1.MinHz);
        Assert.Equal(16000, w1.MaxHz);
        Assert.Equal(0.4, w1.Smoothing, 6);
        Assert.True(w1.PeakHold);
        Assert.Equal(30, w1.PeakDecayDbPerSecond, 6);
        Assert.Equal(HudOrientations.RightToLeft, w1.Orientation);
        Assert.Equal(3, w1.BarGap);
        Assert.Equal(0.75, w1.Opacity, 6);
        Assert.Equal("#FF00A0FF", w1.ColorStart);
        Assert.Equal("#FFFF3060", w1.ColorEnd);

        Assert.Equal(HudWidgetTypes.Volume, back.Widgets[1].Type);
        Assert.False(back.Widgets[1].Visible);
        Assert.Equal(1.5, back.Widgets[1].Scale, 6);
    }

    [Fact]
    public void Save_creates_the_directory_and_replaces_atomically_leaving_no_temp_behind()
    {
        var nested = System.IO.Path.Combine(_dir, "a", "b");
        var path = System.IO.Path.Combine(nested, "hud.json");

        new HudLayout { Fps = 20 }.Save(path);
        Assert.Equal(20, HudLayout.Load(path).Fps);

        new HudLayout { Fps = 15 }.Save(path);
        Assert.Equal(15, HudLayout.Load(path).Fps);

        // temp + rename discipline: exactly the one file, nothing left over.
        Assert.Equal(new[] { "hud.json" }, Directory.GetFiles(nested).Select(System.IO.Path.GetFileName).Order());
    }

    // ---- normalization: this file is user-editable and comes back from disk ----

    [Fact]
    public void Load_normalizes_every_out_of_range_value_it_finds()
    {
        var path = Path_("wild.json");
        File.WriteAllText(path, """
        {
          "Mode": "sideways",
          "Fps": 900,
          "Widgets": [
            { "Id": "", "Type": "spectrum", "Width": -50, "Height": 0, "BandCount": 100000,
              "MinHz": 0, "MaxHz": 5, "Smoothing": 4, "Opacity": 9, "Orientation": "diagonal",
              "BarGap": -3, "Scale": 99, "PeakDecayDbPerSecond": -1 },
            { "Id": "keep", "Type": "not-a-widget", "Width": 100, "Height": 100 }
          ]
        }
        """);

        var layout = HudLayout.Load(path);

        Assert.Equal(HudModes.Live, layout.Mode);          // unknown mode -> the safe one
        Assert.Equal(HudLayout.MaxFps, layout.Fps);
        // The unknown widget TYPE is dropped: there is nothing that can render it, and keeping it
        // would mean an invisible entry that silently reappears in every later save.
        var w = Assert.Single(layout.Widgets);
        Assert.Equal(HudWidgetTypes.Spectrum, w.Type);
        Assert.False(string.IsNullOrEmpty(w.Id));           // a blank id is assigned a fresh one
        Assert.True(w.Width >= HudWidget.MinSize);
        Assert.True(w.Height >= HudWidget.MinSize);
        Assert.InRange(w.BandCount, HudWidget.MinBands, HudWidget.MaxBands);
        Assert.True(w.MinHz >= HudWidget.MinHzLimit);
        Assert.True(w.MaxHz > w.MinHz);
        Assert.InRange(w.Smoothing, 0, 1);
        Assert.InRange(w.Opacity, HudWidget.MinOpacity, 1);
        Assert.Equal(HudOrientations.LeftToRight, w.Orientation);
        Assert.True(w.BarGap >= 0);
        Assert.InRange(w.Scale, HudWidget.MinScale, HudWidget.MaxScale);
        Assert.True(w.PeakDecayDbPerSecond > 0);
    }

    [Fact]
    public void Duplicate_widget_ids_are_made_unique_on_load()
    {
        var path = Path_("dupes.json");
        File.WriteAllText(path, """
        { "Widgets": [ { "Id": "same", "Type": "levels" }, { "Id": "same", "Type": "levels" } ] }
        """);

        var layout = HudLayout.Load(path);

        Assert.Equal(2, layout.Widgets.Count);
        Assert.NotEqual(layout.Widgets[0].Id, layout.Widgets[1].Id);
    }

    [Fact]
    public void More_widgets_than_the_cap_are_refused_rather_than_opening_hundreds_of_windows()
    {
        var many = string.Join(",", Enumerable.Range(0, HudLayout.MaxWidgets + 20)
            .Select(i => $$"""{ "Id": "w{{i}}", "Type": "levels" }"""));
        var path = Path_("many.json");
        File.WriteAllText(path, $$"""{ "Widgets": [ {{many}} ] }""");

        Assert.Equal(HudLayout.MaxWidgets, HudLayout.Load(path).Widgets.Count);
    }

    // ---- defaults per type ----

    [Fact]
    public void Create_gives_each_type_a_sensible_default_box_and_its_own_knobs()
    {
        foreach (var type in HudWidgetTypes.All)
        {
            var w = HudWidget.Create(type);
            Assert.Equal(type, w.Type);
            Assert.False(string.IsNullOrWhiteSpace(w.Id));
            Assert.True(w.Visible);
            Assert.True(w.Width >= HudWidget.MinSize, $"{type} width");
            Assert.True(w.Height >= HudWidget.MinSize, $"{type} height");
        }

        // The spectrum's documented default range is the audible band, log-mapped.
        var spectrum = HudWidget.Create(HudWidgetTypes.Spectrum);
        Assert.Equal(20, spectrum.MinHz);
        Assert.Equal(20000, spectrum.MaxHz);
    }

    [Fact]
    public void Two_created_widgets_never_share_an_id()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => HudWidget.Create(HudWidgetTypes.Levels).Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---- audio consumption: which widgets need the capture running ----

    [Fact]
    public void Only_the_widgets_that_read_audio_count_as_audio_consumers()
    {
        Assert.True(HudWidgetTypes.ConsumesAudio(HudWidgetTypes.Spectrum));
        Assert.True(HudWidgetTypes.ConsumesAudio(HudWidgetTypes.Levels));
        // The EQ curve is drawn from the band chain, and the volume widget from the volume — both
        // are redrawn on change, and neither needs a single sample. A HUD showing only those two
        // must not hold the loopback capture open.
        Assert.False(HudWidgetTypes.ConsumesAudio(HudWidgetTypes.EqCurve));
        Assert.False(HudWidgetTypes.ConsumesAudio(HudWidgetTypes.Volume));
        Assert.False(HudWidgetTypes.ConsumesAudio("something-else"));
    }
}
