using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Names arrive from text boxes AND from aorineq:// links, and end up as path
/// segments, as window text and in Explorer. Two separate jobs live here: deciding whether a
/// name may be accepted at all, and shortening one safely for display.</summary>
public class FileNamesTests
{
    private readonly ITestOutputHelper _out;
    public FileNamesTests(ITestOutputHelper output) => _out = output;

    /// <summary>Bidi overrides are legal in Windows file names and invisible on screen, but they
    /// reverse how the rest of a string renders — so a preset name can display as something it
    /// isn't, in the confirm dialog and everywhere else. They are refused outright rather than
    /// stripped at each display site.</summary>
    // Code points, not literals: these characters are invisible in source (and
    // U+0085 is itself a line terminator to the C# lexer).
    [Theory]
    [InlineData(0x202E)]   // RIGHT-TO-LEFT OVERRIDE — the classic filename spoof
    [InlineData(0x202D)]   // LEFT-TO-RIGHT OVERRIDE
    [InlineData(0x202B)]   // RIGHT-TO-LEFT EMBEDDING
    [InlineData(0x200F)]   // RIGHT-TO-LEFT MARK
    [InlineData(0x2067)]   // RIGHT-TO-LEFT ISOLATE
    [InlineData(0x0085)]   // C1 NEXT LINE (GetInvalidFileNameChars covers 0-31 only)
    public void Names_that_can_disguise_how_they_render_are_refused(int codePoint)
    {
        var name = "Safe preset " + (char)codePoint + "txt";
        var error = FileNames.Validate(name, "Preset name");
        _out.WriteLine($"U+{codePoint:X4} -> {error}");

        Assert.NotNull(error);
        Assert.Contains("disguise", error!);
        Assert.False(FileNames.IsPathSafe(name));
        Assert.NotNull(PresetStore.ValidateName(name));
        Assert.NotNull(SkinWriter.ValidateName(name));
    }

    [Fact]
    public void A_link_carrying_a_disguised_preset_name_is_malformed()
    {
        var data = EqShare.Encode(new EqPreset("x", 0, new[] { new EqBand(EqBandType.Peak, 1000, 1, 1) }));
        var raw = $"aorineq://apply-preset?type=eq&data={data}&name="
            + Uri.EscapeDataString("Safe preset " + (char)0x202E + "txt");
        var result = ProtocolLink.Parse(raw);

        _out.WriteLine($"RLO in name -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("Sennheiser HD 650")]        // ordinary
    [InlineData("café — warm")]              // accented + dash
    [InlineData("Bass 🎧 boost")]            // astral characters
    [InlineData("family 👨‍👩‍👧 preset")]      // zero-width joiners: legitimate, not refused
    public void Ordinary_names_are_still_accepted(string name)
    {
        _out.WriteLine($"'{name}' ({name.Length} code units)");
        Assert.Null(FileNames.Validate(name, "Preset name"));
    }

    [Fact]
    public void Display_truncation_never_cuts_a_character_in_half()
    {
        // Each of these is ONE text element made of several UTF-16 code units. A naive
        // substring would leave half a surrogate pair or an orphaned combining mark.
        const string emoji = "👨‍👩‍👧";           // ZWJ sequence
        var name = string.Concat(Enumerable.Repeat(emoji, 10));

        var shown = FileNames.ForDisplay(name, 5);
        _out.WriteLine($"{name.Length} code units -> '{shown}' ({shown.Length} code units)");

        Assert.EndsWith("…", shown);
        Assert.Equal(4, System.Globalization.StringInfo.ParseCombiningCharacters(shown[..^1]).Length);
        Assert.StartsWith(string.Concat(Enumerable.Repeat(emoji, 4)), shown);
        Assert.DoesNotContain('�', shown);   // no lone surrogate rendered as a replacement
        Assert.False(char.IsHighSurrogate(shown[^2]));
    }

    [Fact]
    public void Display_truncation_leaves_short_names_alone()
    {
        Assert.Equal("HD 650", FileNames.ForDisplay("HD 650", 48));
        Assert.Equal("", FileNames.ForDisplay("anything", 0));
    }

    [Fact]
    public void Display_truncation_shortens_a_long_plain_name()
    {
        var name = new string('a', 200);
        var shown = FileNames.ForDisplay(name, 48);
        _out.WriteLine($"{shown.Length}: {shown}");

        Assert.Equal(48, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void Path_display_keeps_the_folder_that_says_which_skin_it_is()
    {
        // The real shape: every skin names its files empty/full/muted, so the file name alone
        // would not identify the skin, and the full path does not fit the designer's column.
        var full = Path.Combine(Path.GetTempPath(), "AorinEQ", "skins", "seia-bar-shadow", "empty.png");
        var shown = FileNames.PathForDisplay(full);
        _out.WriteLine($"{full}  ->  {shown}");

        Assert.Equal(Path.Combine("seia-bar-shadow", "empty.png"), shown);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), shown);
    }

    [Fact]
    public void Path_display_falls_back_when_there_is_no_folder_to_name()
    {
        _out.WriteLine(FileNames.PathForDisplay("empty.png"));

        Assert.Equal("empty.png", FileNames.PathForDisplay("empty.png"));
        // A folder path has no file name to show; showing it whole beats showing nothing.
        var folder = Path.Combine("C:", "skins") + Path.DirectorySeparatorChar;
        Assert.Equal(folder, FileNames.PathForDisplay(folder));
        Assert.Equal("", FileNames.PathForDisplay(""));
    }

    [Fact]
    public void Path_display_is_shorter_than_the_designer_column_for_a_real_appdata_path()
    {
        // The bug this exists for: 60+ characters of path ellipsized down to a few, in a column
        // ~334px wide (~60 characters at the 11pt caption size the panel uses).
        var full = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AorinEQ", "skins", "seia-bar-shadow", "full.png");
        var shown = FileNames.PathForDisplay(full);
        _out.WriteLine($"{full.Length} chars -> {shown.Length} chars: {shown}");

        Assert.True(full.Length > 40, $"fixture is not a realistic long path: {full}");
        Assert.True(shown.Length <= 40, $"still too long for the column: {shown}");
        Assert.EndsWith("full.png", shown);
    }
}
