using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ProtocolLinkTests
{
    private readonly ITestOutputHelper _out;
    public ProtocolLinkTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Parse_full_install_skin_link()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https%3A%2F%2Fexample.com%2Fskins%2Fneon-bar.zip"
            + "&name=neon&sha256=" + new string('a', 64));
        _out.WriteLine($"status={result.Status} link={result.Link}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.NotNull(result.Link);
        Assert.Equal("install-skin", result.Link!.Action);
        Assert.Equal("https://example.com/skins/neon-bar.zip", result.Link.Url);
        Assert.Equal("neon", result.Link.Name);
        Assert.Equal(new string('a', 64), result.Link.Sha256);
    }

    [Fact]
    public void Parse_minimal_link_defaults_name_to_zip_stem()
    {
        var result = ProtocolLink.Parse("apo-volume://install-skin?url=https://example.com/cool-skin.zip");
        _out.WriteLine($"status={result.Status} link={result.Link}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal("cool-skin", result.Link!.Name);
        Assert.Null(result.Link.Sha256);
    }

    [Fact]
    public void Parse_unescapes_percent_encoded_zip_stem()
    {
        var result = ProtocolLink.Parse("apo-volume://install-skin?url=https://example.com/my%20skin.zip");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal("my skin", result.Link!.Name);
    }

    [Theory]
    [InlineData("apo-volume://install-skin?url=http://example.com/skin.zip")]  // http downgrade
    [InlineData("apo-volume://install-skin?url=file:///C:/evil/skin.zip")]     // local file
    [InlineData("apo-volume://install-skin?url=ftp://example.com/skin.zip")]   // other scheme
    [InlineData("apo-volume://install-skin?url=javascript:alert(1)")]          // not even a host
    public void Parse_rejects_non_https_download_urls(string raw)
    {
        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"{raw} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
        Assert.Null(result.Link);
    }

    [Fact]
    public void Parse_rejects_credentials_in_download_url()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://user:pass@example.com/skin.zip");
        _out.WriteLine($"credential url -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("apo-volume://install-skin")]                       // no url at all
    [InlineData("apo-volume://install-skin?name=foo")]              // url missing
    [InlineData("apo-volume://install-skin?url=")]                  // url empty
    [InlineData("apo-volume://install-skin?url=not a url")]         // unparseable
    [InlineData("not-a-uri-at-all")]                                // garbage
    [InlineData("https://example.com/?fake=apo-volume://install-skin")] // wrong scheme
    [InlineData("")]                                                // empty
    public void Parse_rejects_malformed_links(string raw)
    {
        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"'{raw}' -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("apply-preset")] // reserved for a future version
    [InlineData("do-anything")]
    public void Parse_reports_unknown_actions_distinctly(string action)
    {
        var result = ProtocolLink.Parse($"apo-volume://{action}?url=https://example.com/x.zip");
        _out.WriteLine($"{action} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.UnknownAction, result.Status);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData("con")]           // reserved device name
    [InlineData("a\\/b")]         // path separators
    [InlineData("..")]            // traversal
    [InlineData("ends-with-dot.")]
    public void Parse_rejects_invalid_skin_names(string name)
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://example.com/skin.zip&name=" + Uri.EscapeDataString(name));
        _out.WriteLine($"name '{name}' -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_invalid_url_default_name()
    {
        // No explicit name, and the zip stem is unusable as a folder name -> malformed.
        var result = ProtocolLink.Parse("apo-volume://install-skin?url=https://example.com/nul.zip");
        _out.WriteLine($"reserved stem -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("abc")]                       // too short
    [InlineData("xyzt")]                      // not hex (also short)
    public void Parse_rejects_bad_sha256_values(string sha)
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://example.com/skin.zip&sha256=" + sha);
        _out.WriteLine($"sha '{sha}' -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_non_hex_sha256_of_correct_length()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://example.com/skin.zip&sha256=" + new string('g', 64));
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_normalizes_sha256_to_lowercase()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://example.com/skin.zip&sha256=" + new string('A', 64));
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(new string('a', 64), result.Link!.Sha256);
    }

    [Fact]
    public void Parse_rejects_oversized_links()
    {
        var raw = "apo-volume://install-skin?url=https://example.com/skin.zip&name="
            + new string('x', 5000);
        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"len {raw.Length} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_ignores_unknown_query_parameters()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://install-skin?url=https://example.com/skin.zip&future=stuff");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal("skin", result.Link!.Name);
    }

    [Fact]
    public void Parse_accepts_trailing_slash_before_query()
    {
        // Some browsers normalize scheme://host?x into scheme://host/?x.
        var result = ProtocolLink.Parse("apo-volume://install-skin/?url=https://example.com/skin.zip");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
    }

    [Fact]
    public void Parse_rejects_extra_path_segments()
    {
        var result = ProtocolLink.Parse("apo-volume://install-skin/extra?url=https://example.com/skin.zip");
        _out.WriteLine($"extra path -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void IsProtocolArg_matches_only_our_scheme()
    {
        Assert.True(ProtocolLink.IsProtocolArg("apo-volume://install-skin?url=https://x.com/a.zip"));
        Assert.True(ProtocolLink.IsProtocolArg("APO-VOLUME://install-skin"));
        Assert.False(ProtocolLink.IsProtocolArg("--settings"));
        Assert.False(ProtocolLink.IsProtocolArg("http://apo-volume://nested"));
        Assert.False(ProtocolLink.IsProtocolArg(""));
    }

    [Theory]
    [InlineData("apo-volume://install-skin?url=https://x.com/a.zip", true)]
    [InlineData("apo-volume://install-skin?url=https://x.com/a.zip\" --evil", false)] // quote smuggling
    [InlineData("apo-volume://install-skin?url=https://x.com/a b.zip", false)]        // raw space splits args
    public void IsSafeToForward_blocks_quote_and_whitespace_smuggling(string arg, bool expected)
    {
        // The elevation bounce joins forwarded args with spaces and no quoting — an arg
        // containing whitespace or quotes could smuggle extra arguments into the elevated child.
        Assert.Equal(expected, ProtocolLink.IsSafeToForward(arg));
    }
}
