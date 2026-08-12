using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Downloads the official Equalizer APO installer at first run. Downloading (rather
/// than bundling) keeps AorinEQ free of GPLv2 redistribution obligations — the user's
/// machine fetches EAPO from its official home.
///
/// SourceForge specifics, verified live: the human-facing "latest/download" and
/// ".../files/.../download" URLs serve an HTML interstitial to browser-like clients.
/// <c>best_release.json</c> names the current installer file, and
/// <c>downloads.sourceforge.net</c> redirects straight to a mirror's binary when the request
/// carries a CLI-style User-Agent. Belt and braces: the downloaded bytes are only accepted if
/// the response isn't HTML and the file starts with the PE "MZ" magic — under no circumstances
/// can a mis-served page end up executed as an installer.</summary>
public static class InstallerDownload
{
    /// <summary>Machine-readable descriptor of the project's current release.</summary>
    public const string BestReleaseUrl = "https://sourceforge.net/projects/equalizerapo/best_release.json";

    private const string DirectHost = "https://downloads.sourceforge.net/project/equalizerapo";

    // SourceForge sniffs the User-Agent: browsers get the interstitial page, CLI agents get a
    // 302 to the mirror binary. Wget's token is the reliably-supported CLI shape.
    private const string CliUserAgent = "Wget/1.21";

    /// <summary>Resolves the direct-download URL of the newest installer via
    /// <paramref name="bestReleaseUrl"/> (parameterized for tests; callers use
    /// <see cref="BestReleaseUrl"/>). Throws <see cref="InvalidOperationException"/> with a
    /// user-readable message when the metadata can't be fetched or parsed.</summary>
    public static async Task<string> ResolveLatestUrlAsync(string bestReleaseUrl,
        CancellationToken cancellationToken = default)
    {
        using var client = NewClient();
        try
        {
            var json = await client.GetStringAsync(bestReleaseUrl, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var filename = doc.RootElement.GetProperty("release").GetProperty("filename").GetString();
            if (string.IsNullOrEmpty(filename) || !filename.StartsWith('/'))
                throw new InvalidOperationException("Release metadata did not name an installer file.");
            return DirectHost + filename;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
            or InvalidOperationException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                "Couldn't determine the latest Equalizer APO version — check your connection, "
                + "or install manually from equalizerapo.com.", ex);
        }
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="destinationPath"/>, reporting
    /// 0..1 progress (indeterminate -1 reports when the server sends no length). The result is
    /// accepted only if the response is not HTML and the file begins with the PE "MZ" magic.
    /// Throws <see cref="InvalidOperationException"/> with a user-readable message on any
    /// failure; a partial or rejected file is deleted.</summary>
    public static async Task DownloadAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var client = NewClient();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Download failed: server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The download server returned a web page instead of the installer — "
                    + "install manually from equalizerapo.com.");

            long? total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(destinationPath))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    progress?.Report(total is > 0 ? (double)readTotal / total.Value : -1);
                }
            }

            if (!IsPeExecutable(destinationPath))
                throw new InvalidOperationException(
                    "The downloaded file is not a Windows installer — "
                    + "install manually from equalizerapo.com.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            TryDelete(destinationPath);
            if (ex is InvalidOperationException)
                throw;
            throw new InvalidOperationException(
                ex is TaskCanceledException && !cancellationToken.IsCancellationRequested
                    ? "Download timed out — check your connection and try again."
                    : $"Download failed: {ex.Message}", ex);
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10); // large file on slow links; cancellation still applies
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CliUserAgent);
        return client;
    }

    private static bool IsPeExecutable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.Length > 2 && fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
