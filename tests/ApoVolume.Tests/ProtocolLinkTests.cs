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
    [InlineData("do-anything")]
    [InlineData("set-volume")] // deliberately never implemented, but syntactically a link
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

    [Fact]
    public void A_share_link_carries_base64url_only_so_it_survives_the_elevation_bounce()
    {
        var preset = new EqPreset("Studio", -5.0, new[] { new EqBand(EqBandType.Peak, 1000, -3, 1.41) });
        Assert.True(EqShare.TryBuildShareUrl(preset, out var url, out _));
        Assert.True(ProtocolLink.IsSafeToForward(url), url);
    }

    // ---- apply-preset ----

    [Fact]
    public void Parse_hosted_eq_preset_link()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://apply-preset?type=eq&url=https%3A%2F%2Fexample.com%2FHD650.txt"
            + "&name=HD650&scope=global&sha256=" + new string('A', 64));
        _out.WriteLine($"status={result.Status} link={result.Link}");

        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(ProtocolLink.ApplyPresetAction, result.Link!.Action);
        Assert.Equal("https://example.com/HD650.txt", result.Link.Url);
        Assert.Equal("HD650", result.Link.Name);
        Assert.Equal(EqLinkScopes.Global, result.Link.Scope);
        Assert.Equal(new string('a', 64), result.Link.Sha256);
        Assert.Null(result.Link.Preset); // hosted: nothing is known until it downloads
    }

    [Fact]
    public void Hosted_preset_link_defaults_name_to_the_file_stem_and_scope_to_the_device()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://apply-preset?type=eq&url=https://example.com/presets/HD%20650.txt");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal("HD 650", result.Link!.Name);
        Assert.Equal(EqLinkScopes.Device, result.Link.Scope);
        Assert.Null(result.Link.Sha256);
    }

    [Fact]
    public void Parse_inline_eq_preset_link()
    {
        var preset = new EqPreset("Warm", -2.5, new[]
        {
            new EqBand(EqBandType.LowShelf, 105, 3.5, 0.7),
            new EqBand(EqBandType.Peak, 3000, -2, 2.5),
        });
        var raw = $"apo-volume://apply-preset?type=eq&data={EqShare.Encode(preset)}&name=Warm";

        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"{raw.Length} chars -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Null(result.Link!.Url);
        Assert.Equal("Warm", result.Link.Name);
        Assert.Equal(preset.Serialize(), result.Link.Preset!.Serialize());
    }

    [Fact]
    public void Inline_preset_link_without_a_name_gets_the_shared_default()
    {
        var data = EqShare.Encode(new EqPreset("x", 0, new[] { new EqBand(EqBandType.Peak, 1000, 1, 1) }));
        var result = ProtocolLink.Parse($"apo-volume://apply-preset?type=eq&data={data}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(EqShare.DefaultPresetName, result.Link!.Name);
    }

    [Fact]
    public void Parse_reports_an_unknown_preset_type_as_needing_a_newer_version()
    {
        // The osd (settings bundle) slot is reserved but not implemented.
        var result = ProtocolLink.Parse(
            "apo-volume://apply-preset?type=osd&url=https://example.com/bundle.txt");
        _out.WriteLine($"type=osd -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.UnknownAction, result.Status);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData("apo-volume://apply-preset?url=https://example.com/a.txt")]          // no type
    [InlineData("apo-volume://apply-preset?type=eq")]                                // no source
    [InlineData("apo-volume://apply-preset?type=eq&url=http://example.com/a.txt")]   // http
    [InlineData("apo-volume://apply-preset?type=eq&url=https://u:p@example.com/a.txt")] // credentials
    [InlineData("apo-volume://apply-preset?type=eq&url=https://example.com/nul.txt")]   // reserved stem
    [InlineData("apo-volume://apply-preset?type=eq&url=https://example.com/a.txt&scope=everything")]
    [InlineData("apo-volume://apply-preset?type=eq&url=https://example.com/a.txt&sha256=abc")]
    [InlineData("apo-volume://apply-preset?type=eq&data=!!!notbase64!!!")]           // bad payload
    [InlineData("apo-volume://apply-preset?type=eq&data=&name=x")]                   // empty payload
    public void Parse_rejects_malformed_apply_preset_links(string raw)
    {
        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"{raw} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
        Assert.Null(result.Link);
    }

    [Fact]
    public void Parse_rejects_a_link_carrying_both_a_url_and_inline_data()
    {
        // Which one wins would otherwise be an implementation accident, and the confirm dialog
        // could name a source the applied preset never came from.
        var data = EqShare.Encode(new EqPreset("x", 0, new[] { new EqBand(EqBandType.Peak, 1000, 1, 1) }));
        var result = ProtocolLink.Parse(
            $"apo-volume://apply-preset?type=eq&url=https://example.com/a.txt&data={data}");
        _out.WriteLine($"url+data -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_a_sha256_pin_on_inline_data()
    {
        // A pin verifies a download; there is nothing to verify about a payload that travelled
        // inside the link itself, so accepting one would be theatre.
        var data = EqShare.Encode(new EqPreset("x", 0, new[] { new EqBand(EqBandType.Peak, 1000, 1, 1) }));
        var result = ProtocolLink.Parse(
            $"apo-volume://apply-preset?type=eq&data={data}&sha256=" + new string('a', 64));
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_an_inline_preset_named_something_unusable()
    {
        var data = EqShare.Encode(new EqPreset("x", 0, new[] { new EqBand(EqBandType.Peak, 1000, 1, 1) }));
        var result = ProtocolLink.Parse(
            $"apo-volume://apply-preset?type=eq&data={data}&name=" + Uri.EscapeDataString("..\\evil"));
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    // ---- autoeq ----

    [Fact]
    public void Parse_autoeq_link()
    {
        var result = ProtocolLink.Parse("apo-volume://autoeq?model=" + Uri.EscapeDataString("Sennheiser HD 650"));
        _out.WriteLine($"status={result.Status} model={result.Link?.Model}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(ProtocolLink.AutoEqAction, result.Link!.Action);
        Assert.Equal("Sennheiser HD 650", result.Link.Model);
    }

    [Theory]
    [InlineData("apo-volume://autoeq")]              // no model
    [InlineData("apo-volume://autoeq?model=")]       // empty
    [InlineData("apo-volume://autoeq?model=%20%20")] // whitespace only
    public void Parse_rejects_autoeq_links_without_a_usable_model(string raw)
    {
        var result = ProtocolLink.Parse(raw);
        _out.WriteLine($"{raw} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_an_oversized_autoeq_model()
    {
        var result = ProtocolLink.Parse(
            "apo-volume://autoeq?model=" + new string('m', ProtocolLink.MaxModelLength + 1));
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    [Fact]
    public void Parse_rejects_control_characters_in_an_autoeq_model()
    {
        // A newline in the pre-filled search box would be invisible and could hide the rest
        // of the model name from the user.
        var result = ProtocolLink.Parse(
            "apo-volume://autoeq?model=" + Uri.EscapeDataString("HD\n650"));
        _out.WriteLine($"newline in model -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Malformed, result.Status);
    }

    // ---- open ----

    [Theory]
    [InlineData("eq")]
    [InlineData("settings")]
    [InlineData("designer")]
    [InlineData("skins")]
    public void Parse_open_links_for_every_known_page(string page)
    {
        var result = ProtocolLink.Parse($"apo-volume://open?page={page.ToUpperInvariant()}");
        _out.WriteLine($"page={page} -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(ProtocolLink.OpenAction, result.Link!.Action);
        Assert.Equal(page, result.Link.Page); // normalized to lowercase
    }

    [Fact]
    public void Parse_reports_an_unknown_page_as_needing_a_newer_version()
    {
        var result = ProtocolLink.Parse("apo-volume://open?page=widgets");
        _out.WriteLine($"page=widgets -> {result.Status}");
        Assert.Equal(ProtocolParseStatus.UnknownAction, result.Status);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData("apo-volume://open")]
    [InlineData("apo-volume://open?page=")]
    public void Parse_rejects_open_links_without_a_page(string raw)
    {
        Assert.Equal(ProtocolParseStatus.Malformed, ProtocolLink.Parse(raw).Status);
    }
}
