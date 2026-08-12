namespace ApoVolume.Core;

/// <summary>Outcome of parsing an apo-volume:// link. <see cref="Malformed"/> covers anything
/// that fails strict validation ("Invalid apo-volume link" balloon); <see cref="UnknownAction"/>
/// is a syntactically fine link for an action this version doesn't know ("needs a newer
/// version" balloon).</summary>
public enum ProtocolParseStatus { Ok, Malformed, UnknownAction }

public sealed record ProtocolParseResult(ProtocolParseStatus Status, ProtocolLink? Link);

/// <summary>A validated apo-volume:// link. Parsing is strict and pure — no I/O, no UI: a link
/// either yields fully validated fields (https-only download URL without credentials, a skin
/// name <see cref="SkinWriter.ValidateName"/> accepts, an optional lowercase 64-hex sha256 pin)
/// or it is rejected outright. Nothing downstream re-validates.</summary>
public sealed record ProtocolLink(string Action, string Url, string Name, string? Sha256)
{
    public const string Scheme = "apo-volume";
    public const string InstallSkinAction = "install-skin";

    /// <summary>Hard cap on the raw link length — no legitimate skin link needs more, and it
    /// bounds hostile inputs before any parsing happens.</summary>
    public const int MaxLength = 2048;

    /// <summary>Whether a command-line argument is an apo-volume:// link (by scheme prefix only —
    /// validation is <see cref="Parse"/>'s job). Used to spot the link among process args.</summary>
    public static bool IsProtocolArg(string arg) =>
        arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a protocol arg can safely ride the elevation bounce's unquoted
    /// space-joined forwarding: no whitespace, no quotes, nothing that could smuggle extra
    /// arguments into the elevated child's command line.</summary>
    public static bool IsSafeToForward(string arg) =>
        IsProtocolArg(arg) && arg.Length <= MaxLength
        && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\');

    /// <summary>Strictly parses a raw apo-volume:// link. Never throws.</summary>
    public static ProtocolParseResult Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength)
            return new(ProtocolParseStatus.Malformed, null);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return new(ProtocolParseStatus.Malformed, null);

        // The action rides in the authority slot: apo-volume://<action>?<query>. Anything in the
        // path beyond a bare "/" (browser normalization) is not part of the contract.
        var action = uri.Host;
        if (action.Length == 0 || uri.AbsolutePath is not ("" or "/"))
            return new(ProtocolParseStatus.Malformed, null);
        if (!string.Equals(action, InstallSkinAction, StringComparison.OrdinalIgnoreCase))
            return new(ProtocolParseStatus.UnknownAction, null);

        var query = ParseQuery(uri.Query);
        if (query is null)
            return new(ProtocolParseStatus.Malformed, null);

        // url: REQUIRED, absolute https, no credentials.
        if (!query.TryGetValue("url", out var url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var download)
            || download.Scheme != Uri.UriSchemeHttps
            || download.UserInfo.Length != 0
            || download.Host.Length == 0)
            return new(ProtocolParseStatus.Malformed, null);

        // name: optional, defaults to the zip filename stem; must be a usable folder name.
        string name;
        if (query.TryGetValue("name", out var explicitName))
        {
            name = explicitName.Trim();
        }
        else
        {
            try
            {
                name = Path.GetFileNameWithoutExtension(download.LocalPath).Trim();
            }
            catch (ArgumentException)
            {
                return new(ProtocolParseStatus.Malformed, null); // stem has invalid path chars
            }
        }
        if (SkinWriter.ValidateName(name) is not null)
            return new(ProtocolParseStatus.Malformed, null);

        // sha256: optional integrity pin; exactly 64 hex chars, normalized to lowercase.
        string? sha256 = null;
        if (query.TryGetValue("sha256", out var sha))
        {
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                return new(ProtocolParseStatus.Malformed, null);
            sha256 = sha.ToLowerInvariant();
        }

        return new(ProtocolParseStatus.Ok,
            new ProtocolLink(InstallSkinAction, download.AbsoluteUri, name, sha256));
    }

    /// <summary>Splits a query string into first-wins key/value pairs with percent-decoding.
    /// Returns null when decoding fails (stray '%' sequences).</summary>
    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue; // valueless keys carry nothing this contract wants
            try
            {
                var key = Uri.UnescapeDataString(pair[..eq]);
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                result.TryAdd(key, value);
            }
            catch (ArgumentException) // invalid percent-encoding
            {
                return null;
            }
        }
        return result;
    }
}
