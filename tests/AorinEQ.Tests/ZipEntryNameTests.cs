using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The one entry-name guard every zip AorinEQ extracts goes through: skins (one folder
/// deep allowed) and the VB-Cable package (flat only). Pure string rules, so every hostile shape
/// is listed by name rather than hoped about.</summary>
public class ZipEntryNameTests
{
    private readonly ITestOutputHelper _out;
    public ZipEntryNameTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("readme.txt", "readme.txt")]
    [InlineData("VBCABLE_Setup_x64.exe", "VBCABLE_Setup_x64.exe")]
    [InlineData("vbMmeCable64_win10.inf", "vbMmeCable64_win10.inf")]
    [InlineData("a/b.txt", null)]
    [InlineData("a\\b.txt", null)]
    [InlineData("../evil.txt", null)]
    [InlineData("..\\evil.txt", null)]
    [InlineData("a..b.txt", null)]
    [InlineData("..", null)]
    [InlineData("/abs.txt", null)]
    [InlineData("\\abs.txt", null)]
    [InlineData("C:evil.txt", null)]
    [InlineData("C:\\Windows\\evil.dll", null)]
    [InlineData("folder/", null)]
    [InlineData("./x.txt", null)]
    [InlineData("", null)]
    [InlineData("VBCABLE_Setup_x64.exe\0.txt", null)]
    [InlineData("setup|x.exe", null)]
    [InlineData("setup?.exe", null)]
    public void FlatName_accepts_only_a_bare_file_name(string entry, string? expected)
    {
        var actual = ZipEntryName.FlatName(entry);
        _out.WriteLine($"'{entry}' -> {(actual is null ? "REJECTED" : $"'{actual}'")}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("empty.png", new[] { "empty.png" })]
    [InlineData("my-skin/empty.png", new[] { "my-skin", "empty.png" })]
    [InlineData("my-skin\\empty.png", new[] { "my-skin", "empty.png" })]
    [InlineData("./empty.png", new[] { "empty.png" })]
    [InlineData("a//b.png", new[] { "a", "b.png" })]
    [InlineData("folder/", new[] { "folder" })]
    public void Segments_normalizes_separators_and_drops_dot_segments(string entry, string[] expected)
    {
        var actual = ZipEntryName.Segments(entry);
        _out.WriteLine($"'{entry}' -> [{(actual is null ? "REJECTED" : string.Join(" | ", actual))}]");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("../empty.png")]
    [InlineData("a/../../evil.png")]
    [InlineData("..\\..\\evil.png")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("C:x.png")]
    [InlineData("")]
    public void Segments_refuses_anything_that_could_climb_or_root(string entry)
    {
        var actual = ZipEntryName.Segments(entry);
        _out.WriteLine($"'{entry}' -> {(actual is null ? "REJECTED" : string.Join(" | ", actual))}");
        Assert.Null(actual);
    }
}
