using System.IO;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Color = System.Drawing.Color;

namespace AorinEQ.Tests;

/// <summary>The designer re-targets one SkinView on every edit. Decoding is the expensive part,
/// so it must happen only when the art actually changed - and when it does, a failure must leave
/// the view drawing what it drew before.</summary>
[Collection("Wpf")]
public class SkinViewUpdateTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-skinview-" + Guid.NewGuid().ToString("N"));

    public SkinViewUpdateTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SkinInfo Skin(string name, string json)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 200, 40, Color.Red);
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 200, 40, Color.Lime);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid, info.Error);
        return info;
    }

    [Fact]
    public void Text_fill_and_scale_changes_do_not_decode_the_art_again()
    {
        var info = Skin("a", """{"percentText":{"show":true,"x":5,"y":5}}""");
        var r = _sta.Run(() =>
        {
            var view = new SkinView(info);
            view.SetVolume(50, false);
            var empty = view.EmptyFrames;
            var full = view.FullFrames;
            view.Update(info with { Text = new SkinText(true, 30, 2, FontSize: 20), FillStartX = 20, FillEndX = 180, Scale = 2 });
            return (SameEmpty: ReferenceEquals(empty, view.EmptyFrames), SameFull: ReferenceEquals(full, view.FullFrames),
                view.Width, Top: view.PercentPath.Margin.Top, view.Info.FillStartX, view.PercentPath.Visibility);
        });
        _out.WriteLine($"{r}");
        Assert.True(r.SameEmpty, "the empty layer was decoded again for a text/fill/scale change");
        Assert.True(r.SameFull, "the full layer was decoded again for a text/fill/scale change");
        Assert.Equal(400, r.Width);
        Assert.Equal(4, r.Top);
        Assert.Equal(20, r.FillStartX);
        Assert.Equal(System.Windows.Visibility.Visible, r.Visibility);
    }

    [Fact]
    public void New_art_a_new_frame_count_or_new_bytes_are_decoded()
    {
        var info = Skin("b", "{}");
        var other = Path.Combine(_dir, "other.png");
        RealPngs.WriteSolid(other, 200, 40, Color.Blue);
        var sheet = Path.Combine(_dir, "sheet.png");
        RealPngs.WriteFrames(sheet, 200, 40, [Color.Red, Color.Blue]);

        var r = _sta.Run(() =>
        {
            var view = new SkinView(info);
            var first = view.EmptyFrames;
            view.Update(info with { EmptyPath = other });
            var swapped = view.EmptyFrames;
            view.Update(info with { EmptyPath = sheet, EmptyFrames = 2 });
            var framesNow = view.EmptyFrames.Frames.Count;
            var beforeRewrite = view.FullFrames;
            RealPngs.WriteSolid(info.FullPath, 200, 40, Color.Yellow);
            File.SetLastWriteTimeUtc(info.FullPath, DateTime.UtcNow.AddMinutes(1));
            view.Update(info with { EmptyPath = sheet, EmptyFrames = 2 });
            return (NewArt: !ReferenceEquals(first, swapped), framesNow,
                Rewritten: !ReferenceEquals(beforeRewrite, view.FullFrames));
        });
        _out.WriteLine($"{r}");
        Assert.True(r.NewArt);
        Assert.Equal(2, r.framesNow);
        Assert.True(r.Rewritten, "art rewritten on disk under the same name was not decoded again");
    }

    [Fact]
    public void Hiding_the_number_collapses_it()
    {
        var info = Skin("c", """{"percentText":{"show":true,"x":5,"y":5}}""");
        var visibility = _sta.Run(() =>
        {
            var view = new SkinView(info);
            view.Update(info with { Text = new SkinText(false, 5, 5, FontSize: 30) });
            return view.PercentPath.Visibility;
        });
        _out.WriteLine($"expected Collapsed, actual {visibility}");
        Assert.Equal(System.Windows.Visibility.Collapsed, visibility);
    }

    /// <summary>The percent number keeps the SemiBold it has always had when its skin names no
    /// weight, and a weight it names - Light here - is the weight drawn.</summary>
    [Fact]
    public void The_number_is_drawn_semibold_unless_its_skin_names_a_weight()
    {
        var plain = Skin("weight", """{"percentText":{"show":true,"x":5,"y":5,"fontSize":30}}""");
        var light = Skin("light", """{"percentText":{"show":true,"x":5,"y":5,"fontSize":30,"weight":300}}""");
        var r = _sta.Run(() =>
        {
            double Drawn(SkinInfo info)
            {
                var view = new SkinView(info);
                view.SetVolume(88, false);
                return view.PercentPath.Data.Bounds.Width;
            }
            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(new SkinView(plain)).PixelsPerDip;
            double InkAt(System.Windows.FontWeight weight) =>
                TextInk.Width(SkinClamps.DefaultFontFamily, 30, weight, System.Windows.FontStyles.Normal, "88", dip);
            return (Plain: Drawn(plain), Light: Drawn(light), SemiBold: InkAt(System.Windows.FontWeights.SemiBold),
                Normal: InkAt(System.Windows.FontWeights.Normal), LightInk: InkAt(System.Windows.FontWeights.Light));
        });
        _out.WriteLine($"no weight: drawn {r.Plain:0.###}; expected the SemiBold width {r.SemiBold:0.###}, not the Normal width {r.Normal:0.###}");
        _out.WriteLine($"weight 300: drawn {r.Light:0.###}; expected the Light width {r.LightInk:0.###}");
        Assert.Equal(r.SemiBold, r.Plain, 3);
        Assert.True(Math.Abs(r.Normal - r.Plain) > 0.01, "the number was drawn at Normal weight, lighter than it has always been");
        Assert.Equal(r.LightInk, r.Light, 3);
    }

    [Fact]
    public void A_failed_update_leaves_the_view_as_it_was()
    {
        var info = Skin("d", "{}");
        var broken = Path.Combine(_dir, "broken.png");
        TestPngs.WriteHeaderOnly(broken, 200, 40);

        var r = _sta.Run(() =>
        {
            var view = new SkinView(info);
            var before = view.EmptyFrames;
            var invalid = Record.Exception(() => view.Update(info with { Error = "nope" }));
            var undecodable = Record.Exception(() => view.Update(info with { EmptyPath = broken }));
            return (invalid, undecodable, Same: ReferenceEquals(before, view.EmptyFrames), view.Info);
        });
        _out.WriteLine($"invalid -> {r.invalid?.GetType().Name}; undecodable -> {r.undecodable?.GetType().Name}: {r.undecodable?.Message}");
        Assert.IsType<ArgumentException>(r.invalid);
        Assert.NotNull(r.undecodable);
        Assert.True(r.Same);
        Assert.Equal(info, r.Info);
    }

    [Fact]
    public void Muted_art_can_be_added_and_removed()
    {
        var info = Skin("e", "{}");
        var muted = Path.Combine(_dir, "muted.png");
        RealPngs.WriteSolid(muted, 200, 40, Color.Gray);

        var r = _sta.Run(() =>
        {
            var view = new SkinView(info);
            view.Update(info with { MutedPath = muted });
            bool added = view.MutedFrames is not null;
            view.Update(info);
            return (added, Removed: view.MutedFrames is null);
        });
        _out.WriteLine($"expected added=True removed=True; actual added={r.added} removed={r.Removed}");
        Assert.True(r.added);
        Assert.True(r.Removed);
    }
}
