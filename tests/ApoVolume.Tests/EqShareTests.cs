using System.Text;
using ApoVolume.Core;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>The compact share payload is the wire format third parties encode against and the
/// only part of a share link that carries untrusted structured data, so it gets the same
/// treatment as any other parser here: every field validated, every number clamped, hostile
/// shapes (oversized, non-base64, non-UTF8, absurd band counts) rejected outright rather than
/// half-applied.</summary>
public class EqShareTests
{
    private readonly ITestOutputHelper _out;
    public EqShareTests(ITestOutputHelper output) => _out = output;

    private static EqPreset EveryBandType() => new("HD 650", -6.1, new[]
    {
        new EqBand(EqBandType.LowShelf, 105, -1.4, 0.7),
        new EqBand(EqBandType.Peak, 1234.56, -3.25, 1.41),
        new EqBand(EqBandType.HighShelf, 8000, 4.5, 0.71),
        new EqBand(EqBandType.Notch, 60, 0, 30),
        new EqBand(EqBandType.LowPass, 19000, 0, 0.707),
        new EqBand(EqBandType.HighPass, 22, 0, 0.5),
    });

    [Fact]
    public void Roundtrips_every_band_type_negative_gains_and_fractional_q()
    {
        var preset = EveryBandType();
        var data = EqShare.Encode(preset);
        _out.WriteLine($"payload ({data.Length} chars): {data}");
        _out.WriteLine($"decoded from: {Encoding.UTF8.GetString(FromBase64Url(data))}");

        Assert.True(EqShare.TryDecode(data, preset.Name, out var decoded, out var error), error);
        Assert.Null(error);
        Assert.Equal(preset.Name, decoded.Name);
        Assert.Equal(preset.PreampDb, decoded.PreampDb);
        Assert.Equal(preset.Bands, decoded.Bands);
        // The stronger contract: what EAPO would receive is byte-identical either way.
        Assert.Equal(preset.Serialize(), decoded.Serialize());
    }

    [Fact]
    public void Payload_uses_only_url_safe_characters()
    {
        var data = EqShare.Encode(EveryBandType());
        Assert.All(data, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"'{c}' needs percent-encoding"));
    }

    [Fact]
    public void A_full_24_band_preset_fits_the_link_cap_comfortably()
    {
        var bands = Enumerable.Range(0, 24)
            .Select(i => new EqBand(EqBandType.Peak, 20 * Math.Pow(2, i / 3.0), -4.5 + i * 0.3, 1.41))
            .ToArray();
        Assert.True(EqShare.TryBuildShareUrl(new EqPreset("Studio", -5.2, bands), out var url, out var error), error);
        _out.WriteLine($"24-band share url is {url.Length} chars (cap {ProtocolLink.MaxLength})");
        Assert.True(url.Length < ProtocolLink.MaxLength / 2,
            $"24 bands should use well under half the cap, used {url.Length}");
    }

    [Fact]
    public void Share_url_parses_back_into_the_same_preset()
    {
        var preset = EveryBandType();
        Assert.True(EqShare.TryBuildShareUrl(preset, out var url, out _));
        _out.WriteLine(url);

        var result = ProtocolLink.Parse(url);
        Assert.Equal(ProtocolParseStatus.Ok, result.Status);
        Assert.Equal(ProtocolLink.ApplyPresetAction, result.Link!.Action);
        Assert.Equal(preset.Name, result.Link.Name);
        Assert.Equal(preset.Serialize(), result.Link.Preset!.Serialize());
    }

    [Fact]
    public void Share_url_omits_an_unsaved_preset_name_so_the_receiver_names_it()
    {
        Assert.True(EqShare.TryBuildShareUrl(
            new EqPreset(EqPreset.CustomName, 0, EveryBandType().Bands), out var url, out _));
        _out.WriteLine(url);
        Assert.DoesNotContain("name=", url);

        var link = ProtocolLink.Parse(url).Link!;
        Assert.Equal(EqShare.DefaultPresetName, link.Name);
        Assert.Equal(EqShare.DefaultPresetName, link.Preset!.Name);
    }

    [Fact]
    public void A_chain_too_large_for_a_link_is_refused_with_a_reason()
    {
        var bands = Enumerable.Range(0, EqPreset.MaxBands)
            .Select(i => new EqBand(EqBandType.Peak, 1000.123456 + i, -12.987654, 4.246813))
            .ToArray();
        bool ok = EqShare.TryBuildShareUrl(new EqPreset("Huge", -12.5, bands), out var url, out var error);
        _out.WriteLine($"{EqPreset.MaxBands} worst-case bands -> ok={ok} len={url.Length} error={error}");
        if (!ok)
        {
            Assert.NotNull(error);
            Assert.Equal("", url);
        }
        else
        {
            Assert.True(url.Length <= ProtocolLink.MaxLength);
        }
    }

    [Fact]
    public void Rejects_an_oversized_payload_before_decoding_it()
    {
        var data = new string('A', EqShare.MaxPayloadChars + 1);
        Assert.False(EqShare.TryDecode(data, "x", out _, out var error));
        _out.WriteLine(error);
        Assert.Contains("too large", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("!!!!")]                   // not base64 at all
    [InlineData("YWJj+/==")]               // standard base64 alphabet, not base64url
    [InlineData("YWJjZA")]                 // valid base64url, but not our format
    [InlineData("=")]                      // padding only
    public void Rejects_malformed_payloads(string data)
    {
        Assert.False(EqShare.TryDecode(data, "x", out var preset, out var error));
        _out.WriteLine($"'{data}' -> {error}");
        Assert.NotNull(error);
        Assert.Empty(preset.Bands);
    }

    [Fact]
    public void Rejects_bytes_that_are_not_valid_utf8()
    {
        // 0xC3 starts a 2-byte sequence that never completes — a replacement-character decode
        // would silently turn hostile bytes into a "valid" payload.
        var data = ToBase64Url(new byte[] { (byte)'v', (byte)'1', (byte)'|', 0xC3, 0x28 });
        Assert.False(EqShare.TryDecode(data, "x", out _, out var error));
        _out.WriteLine(error);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("v2|0|PK,1000,0,1", "version")]                  // future format
    [InlineData("|0|PK,1000,0,1", "version")]                    // no version
    [InlineData("v1|0", "no bands")]                             // header only
    [InlineData("v1|0|", "no bands")]                            // empty band list
    [InlineData("v1|abc|PK,1000,0,1", "preamp")]                 // preamp not a number
    [InlineData("v1|0|ZZ,1000,0,1", "filter type")]              // unknown type token
    [InlineData("v1|0|PK,1000,0", "fields")]                     // short band
    [InlineData("v1|0|PK,1000,0,1,9", "fields")]                 // long band
    [InlineData("v1|0|PK,notanumber,0,1", "number")]             // unparseable field
    [InlineData("v1|0|PK,NaN,0,1", "number")]                    // non-finite
    [InlineData("v1|0|PK,Infinity,0,1", "number")]
    public void Rejects_structurally_invalid_payloads(string plain, string expectedInError)
    {
        var data = ToBase64Url(Encoding.UTF8.GetBytes(plain));
        Assert.False(EqShare.TryDecode(data, "x", out var preset, out var error));
        _out.WriteLine($"'{plain}' -> {error}");
        Assert.Contains(expectedInError, error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(preset.Bands);
    }

    [Fact]
    public void Rejects_more_bands_than_a_scope_can_hold()
    {
        var plain = "v1|0|" + string.Join(';',
            Enumerable.Repeat("PK,1000,0,1", EqPreset.MaxBands + 1));
        var data = ToBase64Url(Encoding.UTF8.GetBytes(plain));

        Assert.False(EqShare.TryDecode(data, "x", out _, out var error));
        _out.WriteLine(error);
        Assert.Contains("too many bands", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_exactly_the_maximum_band_count()
    {
        var plain = "v1|0|" + string.Join(';', Enumerable.Repeat("PK,1000,0,1", EqPreset.MaxBands));
        var data = ToBase64Url(Encoding.UTF8.GetBytes(plain));

        Assert.True(EqShare.TryDecode(data, "x", out var preset, out var error), error);
        Assert.Equal(EqPreset.MaxBands, preset.Bands.Count);
    }

    [Fact]
    public void Clamps_absurd_but_parseable_values_instead_of_trusting_them()
    {
        var plain = "v1|-999|PK,999999,500,9999";
        var data = ToBase64Url(Encoding.UTF8.GetBytes(plain));

        Assert.True(EqShare.TryDecode(data, "x", out var preset, out var error), error);
        var band = preset.Bands.Single();
        _out.WriteLine($"preamp={preset.PreampDb} band={band}");
        Assert.Equal(EqPreset.MinPreampDb, preset.PreampDb);
        Assert.Equal(EqPreset.MaxFc, band.Fc);
        Assert.Equal(EqPreset.MaxGainDb, band.GainDb);
        Assert.Equal(EqPreset.MaxQ, band.Q);
    }

    /// <summary>The payload format is a published contract (README → "The `data` payload
    /// format"), so third-party sites encode against it. This pins the worked example in the
    /// docs to the real encoder and decoder, in both directions — a format change that forgets
    /// the documentation fails here.</summary>
    [Fact]
    public void The_documented_example_payload_is_exactly_what_the_codec_produces()
    {
        const string plain = "v1|-6.1|LSC,105,-1.4,0.7;PK,3200,2.6,1.8";
        const string encoded = "djF8LTYuMXxMU0MsMTA1LC0xLjQsMC43O1BLLDMyMDAsMi42LDEuOA";
        var documented = new EqPreset("HD650", -6.1, new[]
        {
            new EqBand(EqBandType.LowShelf, 105, -1.4, 0.7),
            new EqBand(EqBandType.Peak, 3200, 2.6, 1.8),
        });

        Assert.Equal(plain, Encoding.UTF8.GetString(FromBase64Url(encoded)));
        Assert.Equal(encoded, EqShare.Encode(documented));

        Assert.True(EqShare.TryDecode(encoded, "HD650", out var decoded, out var error), error);
        Assert.Equal(documented.Name, decoded.Name);
        Assert.Equal(documented.PreampDb, decoded.PreampDb);
        Assert.Equal(documented.Bands, decoded.Bands); // record equality would compare the list refs
    }

    [Fact]
    public void Decoded_preset_takes_the_name_the_link_carries_not_one_from_the_payload()
    {
        // The payload has no name field at all — the name is a link parameter, validated as a
        // file name before it ever reaches the preset store.
        var data = EqShare.Encode(EveryBandType());
        Assert.True(EqShare.TryDecode(data, "Some Other Name", out var preset, out _));
        Assert.Equal("Some Other Name", preset.Name);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string data)
    {
        var padded = data.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
