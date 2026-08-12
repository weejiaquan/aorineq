using System.Globalization;
using System.Text;

namespace ApoVolume.Core;

/// <summary>Packs an EQ preset into a base64url payload small enough to ride inside an
/// <c>apo-volume://apply-preset</c> link, so a tuning can be shared as a LINK with nothing
/// hosted anywhere — paste it in a forum or a chat, the recipient clicks, the confirm dialog
/// opens.
///
/// The plain text behind the base64url is a documented contract (README) so other sites can
/// generate links:
/// <code>v1|&lt;preamp dB&gt;|&lt;TYPE&gt;,&lt;Fc Hz&gt;,&lt;gain dB&gt;,&lt;Q&gt;;…</code>
/// with the same filter tokens Equalizer APO uses (PK, LSC, HSC, NO, LPQ, HPQ) and invariant
/// shortest-round-trip numbers, which makes encoding exactly lossless while staying about a
/// third the size of the equivalent ParametricEQ text.
///
/// Everything on the decode side is treated as hostile: the payload is length-capped before any
/// work happens, the alphabet is checked before base64 decoding, the bytes must be strict UTF-8,
/// the band count is capped at <see cref="EqPreset.MaxBands"/>, every number must parse finite,
/// and every value is clamped to the model's own limits. A payload either yields a whole preset
/// or is refused with a reason — nothing is ever partially applied.</summary>
public static class EqShare
{
    /// <summary>Format marker. A future format bumps this; older builds then say "needs a newer
    /// version" instead of silently misreading the payload.</summary>
    public const string Version = "v1";

    /// <summary>What a shared preset is called when the link carries no name.</summary>
    public const string DefaultPresetName = "Shared preset";

    /// <summary>Cap on the encoded payload. The link as a whole is capped at
    /// <see cref="ProtocolLink.MaxLength"/>; this bounds the payload before any decoding, and
    /// leaves room for the rest of the URL.</summary>
    public const int MaxPayloadChars = 3600;

    /// <summary>Cap on the decoded bytes. Base64 expands by 4/3, so this can never actually bite
    /// before <see cref="MaxPayloadChars"/> does — it is the belt to that braces, keeping the
    /// decode buffer bounded no matter how the caps are edited later.</summary>
    public const int MaxDecodedBytes = MaxPayloadChars * 3 / 4 + 3;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The compact payload for a preset. The preset NAME is not part of the payload —
    /// it rides as the link's <c>name</c> parameter, where it is validated as a file name before
    /// the preset store ever sees it.</summary>
    public static string Encode(EqPreset preset)
    {
        var sb = new StringBuilder(Version).Append('|').Append(Num(preset.PreampDb)).Append('|');
        for (int i = 0; i < preset.Bands.Count; i++)
        {
            var band = preset.Bands[i];
            if (i > 0)
                sb.Append(';');
            sb.Append(EqPreset.TypeToken(band.Type)).Append(',')
              .Append(Num(band.Fc)).Append(',')
              .Append(Num(band.GainDb)).Append(',')
              .Append(Num(band.Q));
        }
        return ToBase64Url(StrictUtf8.GetBytes(sb.ToString()));
    }

    /// <summary>Decodes a payload into a preset under <paramref name="name"/>, or fails with a
    /// reason. Never throws.</summary>
    public static bool TryDecode(string data, string name, out EqPreset preset, out string? error)
    {
        preset = new EqPreset(name, 0, Array.Empty<EqBand>());
        if (string.IsNullOrEmpty(data))
        {
            error = "The shared preset is empty.";
            return false;
        }
        if (data.Length > MaxPayloadChars)
        {
            error = "The shared preset is too large for a link.";
            return false;
        }
        if (!TryFromBase64Url(data, out var bytes))
        {
            error = "The shared preset isn't valid base64url data.";
            return false;
        }

        string plain;
        try
        {
            plain = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = "The shared preset isn't valid text.";
            return false;
        }

        // Version | preamp | bands. Split with a limit so a stray '|' inside the band list
        // surfaces as a bad band field rather than silently dropping everything after it.
        var parts = plain.Split('|', 3);
        if (parts[0] != Version)
        {
            error = "This share link uses a preset format this version doesn't understand "
                + "(unsupported version).";
            return false;
        }
        if (parts.Length < 2 || !TryReadNumber(parts[1], out double preamp))
        {
            error = "The shared preset's preamp isn't a number.";
            return false;
        }
        var bandList = parts.Length >= 3 ? parts[2] : "";
        if (bandList.Length == 0)
        {
            error = "The shared preset has no bands.";
            return false;
        }

        var fields = bandList.Split(';');
        if (fields.Length > EqPreset.MaxBands)
        {
            error = $"The shared preset has too many bands ({fields.Length}; the limit is "
                + $"{EqPreset.MaxBands}).";
            return false;
        }
        var bands = new List<EqBand>(fields.Length);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!TryReadBand(fields[i], out var band, out var reason))
            {
                error = $"Band {i + 1} of the shared preset is invalid: {reason}";
                return false;
            }
            bands.Add(band);
        }

        preset = new EqPreset(name,
            Math.Clamp(preamp, EqPreset.MinPreampDb, EqPreset.MaxPreampDb), bands);
        error = null;
        return true;
    }

    /// <summary>Builds the full <c>apo-volume://apply-preset</c> share link for a preset, or
    /// fails when the chain simply doesn't fit a URL. The name is carried only when it names a
    /// real saved preset AND would survive the receiver's own validation — an unsaved "(custom)"
    /// chain, or one whose name predates a naming rule, arrives as
    /// <see cref="DefaultPresetName"/> rather than producing a link that won't parse.</summary>
    public static bool TryBuildShareUrl(EqPreset preset, out string url, out string? error)
    {
        var query = new StringBuilder(ProtocolLink.Scheme).Append("://")
            .Append(ProtocolLink.ApplyPresetAction)
            .Append("?type=").Append(ProtocolLink.EqPresetType)
            .Append("&data=").Append(Encode(preset));
        if (preset.Name.Length > 0 && preset.Name != EqPreset.CustomName
            && PresetStore.ValidateName(preset.Name) is null)
            query.Append("&name=").Append(Uri.EscapeDataString(preset.Name));

        var candidate = query.ToString();
        if (candidate.Length > ProtocolLink.MaxLength)
        {
            url = "";
            error = $"This chain is too large to share as a link ({preset.Bands.Count} bands). "
                + "Save it as a preset file and share a hosted link instead.";
            return false;
        }
        url = candidate;
        error = null;
        return true;
    }

    private static bool TryReadBand(string field, out EqBand band, out string reason)
    {
        band = default!;
        var parts = field.Split(',');
        if (parts.Length != 4)
        {
            reason = $"expected 4 comma-separated fields, found {parts.Length}.";
            return false;
        }
        if (EqPreset.ParseTypeToken(parts[0]) is not { } type)
        {
            reason = $"'{parts[0]}' isn't a supported filter type.";
            return false;
        }
        if (!TryReadNumber(parts[1], out double fc) || !TryReadNumber(parts[2], out double gain)
            || !TryReadNumber(parts[3], out double q))
        {
            reason = "frequency, gain and Q must each be a finite number.";
            return false;
        }
        // Gainless types carry no gain in Equalizer APO's grammar at all — drop whatever the
        // payload claimed rather than letting it ride along invisibly.
        band = EqPreset.Clamp(new EqBand(type, fc,
            type is EqBandType.Peak or EqBandType.LowShelf or EqBandType.HighShelf ? gain : 0, q));
        reason = "";
        return true;
    }

    /// <summary>Shortest round-trippable invariant form — exact, and shorter than a fixed
    /// number of decimals for the round values presets are full of.</summary>
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryReadNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Strict base64url decode: the alphabet is checked first (so the padded,
    /// whitespace-tolerant <see cref="Convert.FromBase64String"/> can't accept characters this
    /// format doesn't use), then the standard alphabet and padding are restored.</summary>
    private static bool TryFromBase64Url(string data, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        foreach (var c in data)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }
        if (data.Length % 4 == 1) // no valid base64 group has a single leftover character
            return false;
        var standard = data.Replace('-', '+').Replace('_', '/')
            .PadRight((data.Length + 3) / 4 * 4, '=');
        var buffer = new byte[MaxDecodedBytes];
        if (!Convert.TryFromBase64String(standard, buffer, out int written))
            return false;
        bytes = buffer[..written];
        return true;
    }
}
