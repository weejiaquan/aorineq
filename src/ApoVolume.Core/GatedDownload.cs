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

    // GitHub's API and asset CDN both require a User-Agent.
    private const string UserAgent = "apo-volume";

    public static async Task DownloadAsync(string url, string destinationPath, long maxBytes,
        byte[] magic, string? sha256, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InvalidOperationException("Downloads must use https.");

        using var client = NewClient();
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
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

    private static HttpClient NewClient()
    {
        var client = new HttpClient();
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
