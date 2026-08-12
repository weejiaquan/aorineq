using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The aorineq:// install link the designer's Share action puts on the clipboard. Its
/// whole job is to be pasted somewhere and clicked, so every test here checks it against the
/// SHIPPED parser rather than against a string the test wrote itself.</summary>
public class SkinShareTests
{
    private readonly ITestOutputHelper _out;

    public SkinShareTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Template_parses_as_a_valid_install_skin_link()
    {
        var link = SkinShare.BuildInstallLinkTemplate("neon-bar");
        _out.WriteLine("link: " + link);

        var result = ProtocolLink.Parse(link);
        _out.WriteLine($"parsed: {result.Status} url={result.Link?.Url} name={result.Link?.Name}");
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(ProtocolLink.InstallSkinAction, result.Link!.Action);
        Assert.Equal("neon-bar", result.Link.Name);
        Assert.StartsWith("https://", result.Link.Url);
        Assert.EndsWith(".zip", result.Link.Url);
    }

    [Fact]
    public void Template_carries_no_sha256_because_the_zip_is_not_hosted_yet()
    {
        // The digest pins a file at a URL. Until the author has uploaded the zip there is no such
        // file, and a stale pin would make the link fail for everyone who clicked it.
        var link = SkinShare.BuildInstallLinkTemplate("neon-bar");
        _out.WriteLine("link: " + link);
        Assert.DoesNotContain("sha256", link, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProtocolLink.Parse(link).Link!.Sha256);
    }

    [Fact]
    public void Template_url_is_an_obvious_placeholder_the_author_must_replace()
    {
        var link = SkinShare.BuildInstallLinkTemplate("neon-bar");
        _out.WriteLine("link: " + link);
        Assert.Contains(SkinShare.PlaceholderHost, ProtocolLink.Parse(link).Link!.Url!);
    }

    [Theory]
    [InlineData("My Skin 2")]
    [InlineData("café-bar")]
    [InlineData("100% volume")]
    [InlineData("a&b=c")]
    [InlineData("with#hash")]
    public void Names_that_need_escaping_still_round_trip_through_the_parser(string name)
    {
        Assert.Null(SkinWriter.ValidateName(name)); // precondition: these are all legal skin names

        var link = SkinShare.BuildInstallLinkTemplate(name);
        _out.WriteLine($"'{name}' -> {link}");

        var result = ProtocolLink.Parse(link);
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(name, result.Link!.Name);
    }

    [Fact]
    public void Template_survives_the_elevation_bounce_argument_check()
    {
        // A link the shell hands us is forwarded to an elevated child as an unquoted argument;
        // whitespace or quotes in it would smuggle extra arguments. Escaping is what prevents it.
        var link = SkinShare.BuildInstallLinkTemplate("My Skin 2");
        _out.WriteLine("link: " + link);
        Assert.True(ProtocolLink.IsProtocolArg(link));
        Assert.True(ProtocolLink.IsSafeToForward(link),
            "a shared link must be safe to forward across the elevation bounce");
    }

    [Fact]
    public void Template_stays_within_the_protocol_length_cap()
    {
        var link = SkinShare.BuildInstallLinkTemplate(new string('n', FileNames.MaxLength));
        _out.WriteLine($"longest legal name -> {link.Length} chars (cap {ProtocolLink.MaxLength})");
        Assert.True(link.Length <= ProtocolLink.MaxLength);
        Assert.Equal(ProtocolParseStatus.Ok, ProtocolLink.Parse(link).Status);
    }

    [Fact]
    public void Template_uses_the_current_scheme_not_the_legacy_alias()
    {
        var link = SkinShare.BuildInstallLinkTemplate("neon-bar");
        Assert.StartsWith(ProtocolLink.Scheme + "://", link);
        Assert.DoesNotContain(ProtocolLink.LegacyScheme, link);
    }

    [Fact]
    public void An_invalid_skin_name_is_refused_rather_than_producing_a_dead_link()
    {
        var ex = Assert.Throws<ArgumentException>(() => SkinShare.BuildInstallLinkTemplate("bad/name"));
        _out.WriteLine("refused: " + ex.Message);
    }

    [Fact]
    public void Hosting_hint_tells_the_author_what_they_still_have_to_do()
    {
        _out.WriteLine("hint: " + SkinShare.HostingHint);
        Assert.Contains("https", SkinShare.HostingHint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("", SkinShare.HostingHint.Trim());
    }
}
