using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The one parser every renderer, the preview generator and the colour picker read a
/// skin's colours through. Each spelling a shipped or hand-written skin may contain is pinned, and
/// the HSV maths the picker is built on round-trips every 8-bit colour it is asked about.</summary>
public class SkinColorTests
{
    private readonly ITestOutputHelper _out;

    public SkinColorTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("#F0C", 0xFF, 0xFF, 0x00, 0xCC)]
    [InlineData("#8F0C", 0x88, 0xFF, 0x00, 0xCC)]
    [InlineData("#FF8000", 0xFF, 0xFF, 0x80, 0x00)]
    [InlineData("#80FF8000", 0x80, 0xFF, 0x80, 0x00)]
    [InlineData("  #ff8000 ", 0xFF, 0xFF, 0x80, 0x00)]
    [InlineData("red", 0xFF, 0xFF, 0x00, 0x00)]
    [InlineData("Transparent", 0x00, 0xFF, 0xFF, 0xFF)]
    public void Every_accepted_spelling_parses(string input, int a, int r, int g, int b)
    {
        var parsed = SkinColor.Parse(input);
        _out.WriteLine($"'{input}' -> {parsed?.ToHex() ?? "(null)"}");
        Assert.Equal(new SkinColor((byte)a, (byte)r, (byte)g, (byte)b), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("# FFF")]
    [InlineData("Control")]     // a Windows SYSTEM colour: WPF's converter does not know it either
    [InlineData("not a colour")]
    public void Anything_else_is_null_rather_than_a_guess(string? input)
    {
        var parsed = SkinColor.Parse(input);
        _out.WriteLine($"'{input}' -> {parsed?.ToHex() ?? "(null)"}");
        Assert.Null(parsed);
    }

    [Fact]
    public void Hex_is_always_eight_upper_case_digits()
    {
        _out.WriteLine($"{SkinColor.Format("#0f0")} {SkinColor.Format("#80ff8000")} {SkinColor.Format("bad") ?? "(null)"}");
        Assert.Equal("#FF00FF00", SkinColor.Format("#0f0"));
        Assert.Equal("#80FF8000", SkinColor.Format("#80ff8000"));
        Assert.Null(SkinColor.Format("bad"));
        Assert.Equal("#0A0B0C0D", new SkinColor(0x0A, 0x0B, 0x0C, 0x0D).ToHex());
    }

    [Fact]
    public void Opaque_forces_full_alpha_and_leaves_nonsense_alone()
    {
        _out.WriteLine($"{SkinColor.Opaque("#F2202020")} {SkinColor.Opaque("#336699")} {SkinColor.Opaque("nonsense")}");
        Assert.Equal("#FF202020", SkinColor.Opaque("#F2202020"));
        Assert.Equal("#FF336699", SkinColor.Opaque("#336699"));
        Assert.Equal("nonsense", SkinColor.Opaque("nonsense"));
    }

    [Fact]
    public void Hsv_lands_on_the_primaries()
    {
        var red = SkinColor.FromHsv(0, 1, 1, 255);
        var lime = SkinColor.FromHsv(120, 1, 1, 255);
        var halfBlue = SkinColor.FromHsv(240, 1, 0.5, 128);
        var wrapped = SkinColor.FromHsv(-120, 1, 1, 255);
        _out.WriteLine($"red={red.ToHex()} lime={lime.ToHex()} halfBlue={halfBlue.ToHex()} wrapped={wrapped.ToHex()}");
        Assert.Equal(new SkinColor(255, 255, 0, 0), red);
        Assert.Equal(new SkinColor(255, 0, 255, 0), lime);
        Assert.Equal(new SkinColor(128, 0, 0, 128), halfBlue);
        Assert.Equal(new SkinColor(255, 0, 0, 255), wrapped);
        Assert.Equal((0.0, 0.0, 0.5019607843137255), new SkinColor(255, 128, 128, 128).ToHsv());
    }

    [Fact]
    public void Every_sampled_colour_survives_a_round_trip_through_hsv()
    {
        var failures = new List<string>();
        for (int r = 0; r <= 255; r += 17)
        for (int g = 0; g <= 255; g += 17)
        for (int b = 0; b <= 255; b += 17)
        {
            var c = new SkinColor(0x80, (byte)r, (byte)g, (byte)b);
            var (h, s, v) = c.ToHsv();
            var back = SkinColor.FromHsv(h, s, v, c.A);
            if (back != c) failures.Add($"{c.ToHex()} -> ({h:F3},{s:F3},{v:F3}) -> {back.ToHex()}");
        }
        _out.WriteLine($"{failures.Count} failures" + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(10)) : ""));
        Assert.Empty(failures);
    }

    [Fact]
    public void Recent_colours_are_newest_first_unique_valid_and_capped()
    {
        IReadOnlyList<string>? recents = null;
        for (int i = 0; i < 14; i++)
            recents = SkinColor.PushRecent(recents, $"#FF0000{i:X2}");
        recents = SkinColor.PushRecent(recents, "#ff000005");   // already there, other case: moves to front
        recents = SkinColor.PushRecent(recents, "not a colour"); // ignored

        _out.WriteLine(string.Join(", ", recents));
        Assert.Equal(SkinColor.RecentCapacity, recents.Count);
        Assert.Equal("#FF000005", recents[0]);
        Assert.Equal("#FF00000D", recents[1]);
        Assert.Single(recents, c => c == "#FF000005");
        Assert.DoesNotContain("#FF000000", recents); // the oldest fell off the end
    }

    [Fact]
    public void Normalizing_stored_recents_drops_garbage_and_duplicates()
    {
        var normalized = SkinColor.NormalizeRecents(["#0f0", "junk", "#FF00FF00", "#336699"]);
        _out.WriteLine(string.Join(", ", normalized));
        Assert.Equal(new[] { "#FF00FF00", "#FF336699" }, normalized);
        Assert.Empty(SkinColor.NormalizeRecents(null));
    }
}
