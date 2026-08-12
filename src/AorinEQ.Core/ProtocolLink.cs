namespace AorinEQ.Core;

/// <summary>Outcome of parsing an aorineq:// link. <see cref="Malformed"/> covers anything
/// that fails strict validation ("Invalid AorinEQ link" balloon); <see cref="UnknownAction"/>
/// is a syntactically fine link for an action — or an action's payload kind — this version
/// doesn't know ("needs a newer version" balloon).</summary>
public enum ProtocolParseStatus { Ok, Malformed, UnknownAction }

public sealed record ProtocolParseResult(ProtocolParseStatus Status, ProtocolLink? Link);

/// <summary>Which EQ scope an <c>apply-preset</c> link targets. Strings (not an enum) to match
/// the URL contract literally, the way <see cref="VolumeModes"/> does for settings.json.</summary>
public static class EqLinkScopes
{
    /// <summary>The device the user is listening on right now — the default.</summary>
    public const string Device = "device";
    /// <summary>The global chain, applied on top of every device.</summary>
    public const string Global = "global";

    public static bool IsValid(string scope) => scope is Device or Global;
}

/// <summary>Windows an <c>open</c> link can bring up. Opening a window changes no state, which
/// is why these links need no confirmation.</summary>
public static class ProtocolPages
{
    public const string Eq = "eq";
    public const string Settings = "settings";
    public const string Designer = "designer";
    public const string Skins = "skins";

    public static bool IsKnown(string page) => page is Eq or Settings or Designer or Skins;
}

/// <summary>A validated aorineq:// link. Parsing is strict and pure — no I/O, no UI: a link
/// either yields fully validated fields (https-only download URL without credentials, a name
/// the relevant store accepts, an optional lowercase 64-hex sha256 pin, a fully decoded inline
/// preset) or it is rejected outright. Nothing downstream re-validates.</summary>
public sealed record ProtocolLink(string Action)
{
    public const string Scheme = "aorineq";

    /// <summary>The pre-v3.0.0 scheme, kept registered as an ALIAS so links already written
    /// somewhere keep working. It is accepted here and nowhere else special: a legacy link parses
    /// into exactly the same <see cref="ProtocolLink"/> as its current-scheme twin, so there is
    /// one handler and no second code path. Nothing in the app ever EMITS it — see
    /// <see cref="EqShare.TryBuildShareUrl"/>.</summary>
    public const string LegacyScheme = "apo-volume";

    public const string InstallSkinAction = "install-skin";
    public const string ApplyPresetAction = "apply-preset";
    public const string AutoEqAction = "autoeq";
    public const string OpenAction = "open";

    /// <summary>The only <c>apply-preset</c> payload kind this version applies. Other values
    /// (an <c>osd</c> settings bundle, say) are reserved and report
    /// <see cref="ProtocolParseStatus.UnknownAction"/>.</summary>
    public const string EqPresetType = "eq";

    /// <summary>Hard cap on the raw link length. Sized for the largest inline preset payload a
    /// share link carries; it bounds hostile inputs before any parsing happens.</summary>
    public const int MaxLength = 4000;

    /// <summary>Cap on an <c>autoeq</c> model string — long enough for every AutoEq model name,
    /// short enough that nothing else can ride in the parameter.</summary>
    public const int MaxModelLength = 120;

    /// <summary>The https resource to fetch: the skin zip for <c>install-skin</c>, the
    /// ParametricEQ text for a hosted <c>apply-preset</c>. Null for inline and non-download
    /// actions.</summary>
    public string? Url { get; init; }

    /// <summary>Skin folder name / preset name, already validated for its store.</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional lowercase 64-hex integrity pin for <see cref="Url"/>.</summary>
    public string? Sha256 { get; init; }

    /// <summary>The decoded inline preset of an <c>apply-preset…&amp;data=</c> link. Decoding is
    /// part of parsing precisely because it is pure — a payload that can't be decoded is a
    /// malformed link, not a runtime failure.</summary>
    public EqPreset? Preset { get; init; }

    /// <summary>Target scope of an <c>apply-preset</c> link (<see cref="EqLinkScopes"/>).</summary>
    public string Scope { get; init; } = EqLinkScopes.Device;

    /// <summary>The model an <c>autoeq</c> link pre-searches for.</summary>
    public string? Model { get; init; }

    /// <summary>The window an <c>open</c> link shows (<see cref="ProtocolPages"/>).</summary>
    public string? Page { get; init; }

    /// <summary>Whether a command-line argument is one of our links (by scheme prefix only —
    /// validation is <see cref="Parse"/>'s job). Used to spot the link among process args.
    /// Accepts <see cref="LegacyScheme"/> too, because that class is still registered and the
    /// shell will hand us such an arg.</summary>
    public static bool IsProtocolArg(string arg) =>
        arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)
        || arg.StartsWith(LegacyScheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a protocol arg can safely ride the elevation bounce's unquoted
    /// space-joined forwarding: no whitespace, no quotes, nothing that could smuggle extra
    /// arguments into the elevated child's command line.</summary>
    public static bool IsSafeToForward(string arg) =>
        IsProtocolArg(arg) && arg.Length <= MaxLength
        && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\');

    /// <summary>Strictly parses a raw aorineq:// link. Never throws.</summary>
    public static ProtocolParseResult Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength)
            return Malformed;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !(Matches(uri.Scheme, Scheme) || Matches(uri.Scheme, LegacyScheme)))
            return Malformed;

        // The action rides in the authority slot: aorineq://<action>?<query>. Anything in the
        // path beyond a bare "/" (browser normalization) is not part of the contract.
        var action = uri.Host;
        if (action.Length == 0 || uri.AbsolutePath is not ("" or "/"))
            return Malformed;

        var query = ParseQuery(uri.Query);
        if (query is null)
            return Malformed;

        if (Matches(action, InstallSkinAction))
            return ParseInstallSkin(query);
        if (Matches(action, ApplyPresetAction))
            return ParseApplyPreset(query);
        if (Matches(action, AutoEqAction))
            return ParseAutoEq(query);
        if (Matches(action, OpenAction))
            return ParseOpen(query);
        return Unknown;
    }

    // ---- install-skin ----

    private static ProtocolParseResult ParseInstallSkin(Dictionary<string, string> query)
    {
        if (ReadDownloadUrl(query) is not { } download)
            return Malformed;
        if (ReadName(query, download, SkinWriter.ValidateName) is not { } name)
            return Malformed;
        if (!TryReadSha256(query, out var sha256))
            return Malformed;

        return new(ProtocolParseStatus.Ok, new ProtocolLink(InstallSkinAction)
        {
            Url = download.AbsoluteUri,
            Name = name,
            Sha256 = sha256,
        });
    }

    // ---- apply-preset ----

    private static ProtocolParseResult ParseApplyPreset(Dictionary<string, string> query)
    {
        // type is required and names the payload kind. An unknown kind is a link for a newer
        // version, not a broken one.
        if (!query.TryGetValue("type", out var type) || type.Length == 0)
            return Malformed;
        if (!Matches(type, EqPresetType))
            return Unknown;

        bool hasUrl = query.ContainsKey("url");
        bool hasData = query.ContainsKey("data");
        // Exactly one source. Both together would leave which one wins to chance, and a link
        // with neither carries no preset at all.
        if (hasUrl == hasData)
            return Malformed;

        var scope = query.TryGetValue("scope", out var rawScope)
            ? rawScope.ToLowerInvariant()
            : EqLinkScopes.Device;
        if (!EqLinkScopes.IsValid(scope))
            return Malformed;

        if (hasData)
        {
            // A sha256 pin verifies a download; there is nothing to verify about a payload that
            // travelled inside the link itself.
            if (query.ContainsKey("sha256"))
                return Malformed;
            var name = query.TryGetValue("name", out var explicitName) ? explicitName.Trim()
                : EqShare.DefaultPresetName;
            if (PresetStore.ValidateName(name) is not null)
                return Malformed;
            if (!EqShare.TryDecode(query["data"], name, out var preset, out _))
                return Malformed;

            return new(ProtocolParseStatus.Ok, new ProtocolLink(ApplyPresetAction)
            {
                Name = name,
                Preset = preset,
                Scope = scope,
            });
        }

        if (ReadDownloadUrl(query) is not { } download)
            return Malformed;
        if (ReadName(query, download, PresetStore.ValidateName) is not { } hostedName)
            return Malformed;
        if (!TryReadSha256(query, out var sha256))
            return Malformed;

        return new(ProtocolParseStatus.Ok, new ProtocolLink(ApplyPresetAction)
        {
            Url = download.AbsoluteUri,
            Name = hostedName,
            Sha256 = sha256,
            Scope = scope,
        });
    }

    // ---- autoeq ----

    private static ProtocolParseResult ParseAutoEq(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("model", out var model))
            return Malformed;
        model = model.Trim();
        if (model.Length == 0 || model.Length > MaxModelLength
            || model.Any(char.IsControl))
            return Malformed;

        return new(ProtocolParseStatus.Ok, new ProtocolLink(AutoEqAction) { Model = model });
    }

    // ---- open ----

    private static ProtocolParseResult ParseOpen(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("page", out var page) || page.Length == 0)
            return Malformed;
        page = page.Trim().ToLowerInvariant();
        // A page this build doesn't have is a link for a newer version: it changes nothing here
        // and the balloon says so, rather than opening some arbitrary window instead.
        if (!ProtocolPages.IsKnown(page))
            return Unknown;

        return new(ProtocolParseStatus.Ok, new ProtocolLink(OpenAction) { Page = page });
    }

    // ---- shared field readers ----

    private static readonly ProtocolParseResult Malformed = new(ProtocolParseStatus.Malformed, null);
    private static readonly ProtocolParseResult Unknown = new(ProtocolParseStatus.UnknownAction, null);

    private static bool Matches(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The required absolute https download URL, without credentials.</summary>
    private static Uri? ReadDownloadUrl(Dictionary<string, string> query) =>
        query.TryGetValue("url", out var url)
        && Uri.TryCreate(url, UriKind.Absolute, out var download)
        && download.Scheme == Uri.UriSchemeHttps
        && download.UserInfo.Length == 0
        && download.Host.Length != 0
            ? download
            : null;

    /// <summary>The explicit <c>name</c>, or the download's filename stem, checked by the
    /// store that will hold it. Null when unusable.</summary>
    private static string? ReadName(Dictionary<string, string> query, Uri download,
        Func<string, string?> validate)
    {
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
                return null; // stem has invalid path chars
            }
        }
        return validate(name) is null ? name : null;
    }

    /// <summary>Optional integrity pin: exactly 64 hex chars, normalized to lowercase.</summary>
    private static bool TryReadSha256(Dictionary<string, string> query, out string? sha256)
    {
        sha256 = null;
        if (!query.TryGetValue("sha256", out var sha))
            return true;
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            return false;
        sha256 = sha.ToLowerInvariant();
        return true;
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
