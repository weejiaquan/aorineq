using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>What the latest published release looks like. Null asset URLs mean the release
/// doesn't carry that asset (or carries it over a non-https URL, which is treated as absent) —
/// an update is only ever offered when BOTH the exe and its sha256 asset resolve.</summary>
public sealed record ReleaseInfo(
    Version Version, string TagName, string? ExeUrl, string? Sha256Url, string HtmlUrl, bool Prerelease);

public enum UpdateStatus { UpToDate, UpdateAvailable, Error }

/// <summary>Outcome of one update check. <see cref="ReleaseInfo"/> is present whenever the feed
/// parsed (even when up to date, so the UI can show "latest vX.Y.Z"); Error carries a readable
/// message for the explicit "Check now" path — background checks just log and move on.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release, string? Error = null);

/// <summary>Checks GitHub Releases for a newer published version. Split from
/// <see cref="UpdateApplier"/>: this class only asks "is there an update?" — pure parsing plus
/// one API GET — and never touches the filesystem.</summary>
public static class UpdateChecker
{
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/weejiaquan/aorineq/releases/latest";

    public const string ExeAssetName = "AorinEQ.exe";
    public const string Sha256AssetName = "AorinEQ.exe.sha256";

    /// <summary>Parses a release tag ("v1.8.0" or "1.8.0") into a Version; null on anything
    /// else — including prerelease-suffixed tags, which are never shipped as /latest.</summary>
    public static Version? ParseVersionTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var text = tag.StartsWith('v') ? tag[1..] : tag;
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <summary>Whether <paramref name="remote"/> is strictly newer than <paramref name="local"/>,
    /// with missing components normalized to zero — a "v1.9.0" tag and the 1.9.0.0 assembly
    /// version are the same version, not an upgrade in either direction.</summary>
    public static bool IsNewer(Version remote, Version local) =>
        Normalize(remote).CompareTo(Normalize(local)) > 0;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Parses the /releases/latest JSON. Null when the payload isn't a release with a
    /// parseable version tag. Asset URLs resolve by exact name and must be https.</summary>
    public static ReleaseInfo? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var version = ParseVersionTag(root.TryGetProperty("tag_name", out var tag)
                ? tag.GetString() : null);
            if (version is null) return null;

            string? exeUrl = null, shaUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps)
                        continue; // a poisoned feed must not redirect the downloader off https
                    if (name == ExeAssetName) exeUrl = url;
                    else if (name == Sha256AssetName) shaUrl = url;
                }
            }

            return new ReleaseInfo(
                version,
                tag.GetString()!,
                exeUrl,
                shaUrl,
                root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One update check: GET the latest-release feed, parse, compare against
    /// <paramref name="localVersion"/>. Never throws. UpdateAvailable requires a strictly newer,
    /// non-prerelease release carrying BOTH assets — anything else that parsed is UpToDate
    /// (the release still reported so the UI can show "latest vX.Y.Z").</summary>
    public static async Task<UpdateCheckResult> CheckAsync(Version localVersion,
        string url = LatestReleaseUrl, CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            // 1 MB cap: the /releases/latest JSON is a few KB; anything vast is a wrong endpoint.
            json = await GatedDownload.GetStringAsync(url, TimeSpan.FromSeconds(10), 1024 * 1024,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or UriFormatException)
        {
            return new(UpdateStatus.Error, null, $"Update check failed: {ex.Message}");
        }

        var release = ParseLatestRelease(json);
        if (release is null)
            return new(UpdateStatus.Error, null, "Update check failed: unexpected response.");

        bool available = !release.Prerelease && IsNewer(release.Version, localVersion)
            && release.ExeUrl is not null && release.Sha256Url is not null;
        return new(available ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate, release);
    }

    /// <summary>Fetches and parses the AorinEQ.exe.sha256 asset. Null on any failure —
    /// without a verified digest no update download ever starts. Same https-or-loopback rule
    /// as <see cref="GatedDownload"/>.</summary>
    public static async Task<string?> FetchSha256Async(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            // Same https-or-loopback gate + hand-followed redirects as the exe download; 4 KB is
            // ample for a one-line digest file.
            return ParseSha256Text(await GatedDownload.GetStringAsync(url, TimeSpan.FromSeconds(30),
                4096, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Parses the AorinEQ.exe.sha256 asset: a lowercase-normalized 64-hex digest,
    /// accepted bare or in sha256sum's "&lt;hex&gt; *name" / "&lt;hex&gt;  name" formats.
    /// Null on anything else.</summary>
    public static string? ParseSha256Text(string text)
    {
        var first = text.Trim().Split(' ', '\t')[0];
        return first.Length == 64 && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }
}
