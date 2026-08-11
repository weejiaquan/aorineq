namespace ApoVolume.Core;

/// <summary>Downloads the official Equalizer APO installer at first run. Downloading (rather
/// than bundling) keeps apo-volume free of GPLv2 redistribution obligations — the user's
/// machine fetches EAPO from its official home.</summary>
public static class InstallerDownload
{
    /// <summary>SourceForge's stable latest-download URL for the EAPO project; it redirects to
    /// the current installer exe.</summary>
    public const string OfficialUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";

    /// <summary>Streams <paramref name="url"/> to <paramref name="destinationPath"/>, reporting
    /// 0..1 progress (falls back to indeterminate -1 reports when the server sends no length).
    /// Throws <see cref="InvalidOperationException"/> with a user-readable message on HTTP or IO
    /// failure; a partial file is deleted.</summary>
    public static async Task DownloadAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10); // large file on slow links; cancellation still applies
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Download failed: server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(destinationPath);
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            try { File.Delete(destinationPath); } catch (IOException) { }
            throw new InvalidOperationException(
                ex is TaskCanceledException && !cancellationToken.IsCancellationRequested
                    ? "Download timed out — check your connection and try again."
                    : $"Download failed: {ex.Message}", ex);
        }
    }
}
