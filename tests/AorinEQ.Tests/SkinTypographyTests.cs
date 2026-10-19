using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Weight, italic and letter spacing - and the notice text's outline and shadow - as the
/// loader reads them and the writer writes them, for every text a skin has: the percent number,
/// both AirPlay labels and the notice name. Real skin folders in a temp directory, real loader,
/// real writer.</summary>
public class SkinTypographyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aorineq-type-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public SkinTypographyTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A skin whose four texts all carry <paramref name="style"/> (a fragment of JSON
    /// members, possibly empty), loaded.</summary>
    private SkinInfo LoadStyled(string name, string style)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 200, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 200, 40);
        TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 200, 24);
        TestPngs.Write(Path.Combine(folder, "airplay-full.png"), 200, 24);
        string more = style.Length == 0 ? "" : ", " + style;
        string json = $$"""
            {
              "percentText": { "show": true, "x": 5, "y": 5{{more}} },
              "airplay": {
                "nameText": { "show": true, "x": 5, "y": 2{{more}} },
                "percentText": { "show": true, "x": 190, "y": 2{{more}} }
              },
              "notice": { "text": { "x": 1{{(style.Length == 0 ? "" : ", " + style)}} } }
            }
            """;
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"{name}: {style}");
        _out.WriteLine($"  percent {info.Text}");
        _out.WriteLine($"  name    {info.AirPlay?.Name}");
        _out.WriteLine($"  level   {info.AirPlay?.Percent}");
        _out.WriteLine($"  notice  {info.Notice?.Text}");
        Assert.True(info.IsValid, info.Error);
        Assert.Null(info.AirPlayError);
        Assert.Null(info.NoticeError);
        return info;
    }

    private static SkinText[] Texts(SkinInfo info) =>
        [info.Text!, info.AirPlay!.Name!, info.AirPlay.Percent!, info.Notice!.Text];

    /// <summary>The legacy "bold" and the new "weight", alone and together, on every text. Each
    /// text's default is the look it had before weights: 600 for the number and the notice name,
    /// 400 for the AirPlay labels.</summary>
    [Theory]
    [InlineData("", 600, 400)]
    [InlineData("\"bold\": false", 600, 400)]
    [InlineData("\"bold\": true", 700, 700)]
    [InlineData("\"bold\": true, \"weight\": 300", 300, 300)]   // weight wins
    [InlineData("\"weight\": 800, \"bold\": false", 800, 800)]
    [InlineData("\"weight\": 500", 500, 500)]
    public void Bold_and_weight_read_as_one_weight_with_each_texts_own_default(string style, int numberAndNotice, int labels)
    {
        var info = LoadStyled($"w{numberAndNotice}-{labels}-{style.Length}", style);
        var weights = Texts(info).Select(t => t.Weight).ToArray();
        _out.WriteLine($"expected [{numberAndNotice}, {labels}, {labels}, {numberAndNotice}], actual [{string.Join(", ", weights)}]");
        Assert.Equal(new[] { numberAndNotice, labels, labels, numberAndNotice }, weights);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(150, 200)]
    [InlineData(449, 400)]
    [InlineData(950, 900)]
    public void A_weight_is_rounded_to_its_hundred_and_clamped_on_every_text(int written, int expected)
    {
        var info = LoadStyled("r" + written, $"\"weight\": {written}");
        var weights = Texts(info).Select(t => t.Weight).ToArray();
        _out.WriteLine($"weight {written}: expected {expected} on all four, actual [{string.Join(", ", weights)}]");
        Assert.All(weights, w => Assert.Equal(expected, w));
    }

    [Theory]
    [InlineData(-50, -20)]
    [InlineData(3.5, 3.5)]
    [InlineData(250, 100)]
    public void Italic_and_letter_spacing_are_read_and_spacing_clamped_on_every_text(double written, double expected)
    {
        var info = LoadStyled("s" + written.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_'),
            $"\"italic\": true, \"letterSpacing\": {written.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var r = Texts(info).Select(t => (t.Italic, t.LetterSpacing)).ToArray();
        _out.WriteLine($"letterSpacing {written}: expected (True, {expected}) on all four, actual [{string.Join(", ", r)}]");
        Assert.All(r, t => Assert.Equal((true, expected), t));
    }

    [Fact]
    public void A_text_that_says_nothing_is_upright_and_unspaced()
    {
        var r = Texts(LoadStyled("plain", "")).Select(t => (t.Italic, t.LetterSpacing)).ToArray();
        _out.WriteLine($"expected (False, 0) on all four, actual [{string.Join(", ", r)}]");
        Assert.All(r, t => Assert.Equal((false, 0.0), t));
    }

    [Fact]
    public void The_notice_text_reads_outline_and_shadow_keys_and_clamps_them()
    {
        var info = LoadStyled("ns", "\"outlineColor\": \"#FF102030\", \"outlineWidth\": 99, \"shadowColor\": \"#80000000\", \"shadowBlur\": 99, \"shadowDepth\": -5");
        var t = info.Notice!.Text;
        _out.WriteLine($"expected outline #FF102030 20, shadow #80000000 blur 50 depth 0; actual outline {t.OutlineColor} {t.OutlineWidth}, " +
                       $"shadow {t.ShadowColor} blur {t.ShadowBlur} depth {t.ShadowDepth}");
        Assert.Equal(("#FF102030", 20.0), (t.OutlineColor, t.OutlineWidth));
        Assert.Equal(("#80000000", 50.0, 0.0), (t.ShadowColor, t.ShadowBlur, t.ShadowDepth));
        Assert.Equal(info.Text! with { X = t.X, Y = t.Y }, t); // read exactly as the percent number reads them
    }

    [Fact]
    public void The_shipped_skins_keep_their_weights()
    {
        DefaultSkins.EnsureInstalled(_root);
        foreach (var style in SkinArt.All)
        {
            var info = SkinLoader.Load(Path.Combine(_root, style));
            var weights = Texts(info).Select(t => t.Weight).ToArray();
            _out.WriteLine($"{style}: [{string.Join(", ", weights)}]; expected [600, 400, 400, 700] - the number SemiBold, the labels Normal, the notice Bold, as they always were");
            Assert.True(info.IsValid, info.Error);
            Assert.Equal(new[] { 600, 400, 400, 700 }, weights);
        }
    }

    private string Save(string name, SkinInfo from)
    {
        var doc = SkinDocument.FromInfo(from);
        return SkinWriter.Save(_root, name, doc.EmptyPath, doc.FullPath, doc.Config, doc.MutedPath);
    }

    /// <summary>A skin written before weights, re-saved, says "weight": 700 where it said "bold":
    /// true - for every text - and never "bold".</summary>
    [Fact]
    public void A_legacy_bold_skin_saved_again_writes_weight_700_and_never_bold()
    {
        var legacy = LoadStyled("legacy", "\"bold\": true");
        var folder = Save("legacy-saved", legacy);
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine(json);
        int weights = json.Split("\"weight\":700").Length - 1;
        Assert.Equal(4, weights);
        Assert.DoesNotContain("bold", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Texts(legacy), Texts(SkinLoader.Load(folder)));
    }

    /// <summary>Defaults are omitted per text: 600 is the number's and the notice's default but a
    /// label's choice, and 400 the other way round; upright and unspaced are never written.</summary>
    [Fact]
    public void A_weight_is_written_only_where_it_is_not_that_texts_default()
    {
        var at600 = LoadStyled("six", "\"weight\": 600");
        var at400 = LoadStyled("four", "\"weight\": 400");
        string Json(SkinInfo info, string name) => File.ReadAllText(Path.Combine(Save(name, info), "skin.json"));
        var six = Json(at600, "six-saved");
        var four = Json(at400, "four-saved");
        _out.WriteLine("600 everywhere: " + six);
        _out.WriteLine("400 everywhere: " + four);
        Assert.Equal(2, six.Split("\"weight\":600").Length - 1);  // the two labels only
        Assert.Equal(2, four.Split("\"weight\":400").Length - 1); // the number and the notice only
        foreach (var json in new[] { six, four })
        {
            Assert.DoesNotContain("italic", json);
            Assert.DoesNotContain("letterSpacing", json);
            Assert.DoesNotContain("bold", json);
        }
    }

    /// <summary>Every new field on every text survives a save and a reload, and a writer-produced
    /// file saves to the same bytes again.</summary>
    [Fact]
    public void Every_type_field_round_trips_on_every_text_and_resaves_byte_identically()
    {
        var styled = LoadStyled("styled",
            "\"weight\": 200, \"italic\": true, \"letterSpacing\": -2.5, \"outlineColor\": \"#FF00FF00\", \"outlineWidth\": 1.5, " +
            "\"shadowColor\": \"#40000000\", \"shadowBlur\": 8, \"shadowDepth\": 3");
        var first = Save("styled-1", styled);
        var reloaded = SkinLoader.Load(first);
        var second = Save("styled-2", reloaded);
        string a = File.ReadAllText(Path.Combine(first, "skin.json")), b = File.ReadAllText(Path.Combine(second, "skin.json"));
        _out.WriteLine(a);
        Assert.Equal(Texts(styled), Texts(reloaded));
        Assert.Equal(a, b);
    }
}
