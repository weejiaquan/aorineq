using System.Security.Cryptography;

namespace ApoVolume.Core;

/// <summary>Downloads untrusted content behind hard gates, shared by the protocol skin install
/// (zip) and the auto-updater (exe): https-only (plain http allowed for loopback only, which is
/// what the in-process listener tests use), a size cap enforced both from the declared length
/// and while streaming, a leading magic-byte check, and an optional sha256 pin. Any gate
/// failure throws <see cref="InvalidOperationException"/> with a readable message and deletes
/// the partial file — a rejected download never lingers on disk.</summary>
public static class GatedDownload
{
    public static readonly byte[] ZipMagic = { (byte)'P', (byte)'K' };
    public static readonly byte[] ExeMagic = { (byte)'M', (byte)'Z' };

    /// <summary>For plain-text downloads (EQ presets), which have no signature to check. The
    /// content gate is the strict parse that follows: anything that isn't a full ParametricEQ
    /// block — an HTML error page, say — is refused there and nothing is applied.</summary>
    public static readonly byte[] NoMagic = Array.Empty<byte>();

    // GitHub's API and asset CDN both require a User-Agent.
    private const string UserAgent = "apo-volume";

    public static async Task DownloadAsync(string url, string destinationPath, long maxBytes,
        byte[] magic, string? sha256, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireAllowedScheme(url);

        // Auto-redirect is OFF: HttpClient's default would silently follow a 302 from an https
        // URL down to http/file, defeating the transport gate. Redirects are instead followed by
        // hand, re-validating the scheme of EVERY hop (GitHub asset URLs legitimately redirect to
        // objects.githubusercontent.com — over https, which is what this enforces).
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = NewClient(handler);
        try
        {
            using var response = await GetFollowingValidatedRedirectsAsync(client, url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Download failed: server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            long? total = response.Content.Headers.ContentLength;
            if (total > maxBytes)
                throw new InvalidOperationException("The download is too large.");

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(destinationPath))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    readTotal += read;
                    if (readTotal > maxBytes)
                        throw new InvalidOperationException("The download is too large.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(total is > 0 ? (double)readTotal / total.Value : -1);
                }
            }

            if (!HasMagic(destinationPath, magic))
                throw new InvalidOperationException("The downloaded file is not the expected file type.");

            if (sha256 is not null)
            {
                var actual = await HashFileAsync(destinationPath, cancellationToken);
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The downloaded file failed its checksum (sha256) verification.");
            }
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

    /// <summary>Lowercase hex sha256 of a file — the format the release pipeline publishes in
    /// ApoVolume.exe.sha256 and the protocol's sha256 pin uses.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>GETs a text resource (the GitHub API feed, the sha256 asset) with the same
    /// scheme gate and hand-followed redirects as the file download, so a poisoned redirect
    /// can't drop the fetch onto http/file either. Throws on any transport failure or bad hop.
    /// The response body is size-capped at <paramref name="maxBytes"/>.</summary>
    public static async Task<string> GetStringAsync(string url, TimeSpan timeout, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        RequireAllowedScheme(url);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        try
        {
            using var response = await GetFollowingValidatedRedirectsAsync(client, url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Request failed: server returned {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException("Response too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > maxBytes)
                throw new InvalidOperationException("Response too large.");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>https, or http to a loopback host (which is what the in-process listener tests
    /// use). Anything else — http to a real host, file, ftp — is rejected. Applied to the
    /// original URL AND every redirect hop.</summary>
    private static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static void RequireAllowedScheme(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedScheme(uri))
            throw new InvalidOperationException("Downloads must use https.");
    }

    /// <summary>GETs <paramref name="url"/> following up to 10 redirects by hand, rejecting any
    /// hop whose scheme isn't allowed (an https URL that 302s to http/file is refused). Shared by
    /// the download and the sha256-asset fetch. Disposes intermediate redirect responses.</summary>
    private static async Task<HttpResponseMessage> GetFollowingValidatedRedirectsAsync(
        HttpClient client, string url, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var current = new Uri(url);
        for (int hop = 0; hop < 10; hop++)
        {
            var response = await client.GetAsync(current, completion, cancellationToken);
            if (!IsRedirect(response.StatusCode))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidOperationException("Download failed: redirect without a target.");
            // Relative redirects resolve against the current absolute URL, keeping the scheme.
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedScheme(current))
                throw new InvalidOperationException(
                    "Download failed: the server redirected to a non-https location.");
        }
        throw new InvalidOperationException("Download failed: too many redirects.");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode code) =>
        (int)code is 301 or 302 or 303 or 307 or 308;

    private static bool HasMagic(string path, byte[] magic)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[magic.Length];
            return fs.Read(head, 0, head.Length) == magic.Length && head.SequenceEqual(magic);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static HttpClient NewClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(10); // large file on slow links; cancellation still applies
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
