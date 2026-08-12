namespace ApoVolume.Core;

/// <summary>One AutoEq results entry: model name, measurement source, and the
/// percent-encoded path under the repo's results/ directory.</summary>
public sealed record AutoEqEntry(string Name, string Source, string RelativePath);

/// <summary>The AutoEq results index and its ParametricEQ downloads (both from the AutoEq
/// GitHub repo over the gated https client — same transport rules as every other download).
/// The index is cached on disk so the search dialog opens instantly; a refresh button
/// refetches on demand, and a failed refetch falls back to the cache.</summary>
public static class AutoEqIndex
{
    public const string IndexUrl =
        "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/INDEX.md";
    private const string ResultsBaseUrl =
        "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";

    private const long MaxIndexBytes = 8 * 1024 * 1024;   // real index is ~0.9 MB
    private const long MaxPresetBytes = 256 * 1024;       // real files are ~500 bytes
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Parses the INDEX.md format: <c>- [Name](./path) by Source on Target</c>.
    /// Model names and paths may contain parentheses, so the path ends at the <c>)</c>
    /// directly before " by " (or at the line's final <c>)</c> when no attribution follows).
    /// Unrecognized lines are skipped.</summary>
    public static IReadOnlyList<AutoEqEntry> ParseIndex(string markdown)
    {
        var entries = new List<AutoEqEntry>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("- [", StringComparison.Ordinal))
                continue;
            int nameEnd = line.IndexOf("](", StringComparison.Ordinal);
            if (nameEnd < 0)
                continue;
            var name = line[3..nameEnd];
            var rest = line[(nameEnd + 2)..];

            string path, source;
            int byMarker = rest.LastIndexOf(") by ", StringComparison.Ordinal);
            if (byMarker >= 0)
            {
                path = rest[..byMarker];
                var attribution = rest[(byMarker + 5)..];
                int on = attribution.IndexOf(" on ", StringComparison.Ordinal);
                source = on >= 0 ? attribution[..on] : attribution;
            }
            else if (rest.EndsWith(')'))
            {
                path = rest[..^1];
                source = "";
            }
            else
            {
                continue;
            }

            if (name.Length == 0 || !path.StartsWith("./", StringComparison.Ordinal))
                continue;
            entries.Add(new AutoEqEntry(name, source.Trim(), path[2..]));
        }
        return entries;
    }

    /// <summary>Raw-file URL of the entry's Equalizer APO ParametricEQ export — the file
    /// AutoEq publishes as "&lt;model dir&gt; ParametricEQ.txt" inside the model directory.</summary>
    public static string ParametricEqUrl(AutoEqEntry entry)
    {
        int lastSlash = entry.RelativePath.LastIndexOf('/');
        var encodedDir = lastSlash >= 0 ? entry.RelativePath[(lastSlash + 1)..] : entry.RelativePath;
        var decodedDir = Uri.UnescapeDataString(encodedDir);
        return ResultsBaseUrl + entry.RelativePath + "/"
            + Uri.EscapeDataString(decodedDir + " ParametricEQ.txt");
    }

    /// <summary>Case-insensitive all-words search over name + source; a blank query lists
    /// everything (the dialog's initial state). Results keep index order, capped at
    /// <paramref name="limit"/>.</summary>
    public static IReadOnlyList<AutoEqEntry> Search(
        IReadOnlyList<AutoEqEntry> entries, string query, int limit)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = new List<AutoEqEntry>();
        foreach (var entry in entries)
        {
            bool all = true;
            foreach (var word in words)
            {
                if (!entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    && !entry.Source.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    all = false;
                    break;
                }
            }
            if (!all)
                continue;
            hits.Add(entry);
            if (hits.Count >= limit)
                break;
        }
        return hits;
    }

    /// <summary>The index text: from the cache unless <paramref name="refresh"/> (or no cache
    /// yet), fetching over the gated client and updating the cache on success. A failed fetch
    /// falls back to the cache when one exists; otherwise throws
    /// <see cref="InvalidOperationException"/> with a readable message.</summary>
    public static async Task<string> FetchIndexAsync(string cachePath, bool refresh,
        string url = IndexUrl, CancellationToken cancellationToken = default)
    {
        if (!refresh && TryReadCache(cachePath) is { } cached)
            return cached;

        string text;
        try
        {
            text = await GatedDownload.GetStringAsync(url, FetchTimeout, MaxIndexBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            if (TryReadCache(cachePath) is { } fallback)
                return fallback;
            throw new InvalidOperationException($"Couldn't fetch the AutoEq index: {ex.Message}", ex);
        }

        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(cachePath, text, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache write is best-effort; the fetched text is still good.
        }
        return text;
    }

    /// <summary>Downloads the entry's ParametricEQ file, validates it actually parses to a
    /// band chain, and lands it in the presets root under the sanitized model name — the saved
    /// file is the repo file byte-for-byte. Returns the parsed preset. Throws
    /// <see cref="InvalidOperationException"/> on transport failure or non-ParametricEQ
    /// content (e.g. an HTML error page).</summary>
    public static async Task<EqPreset> DownloadPresetAsync(AutoEqEntry entry, string presetsRoot,
        string? url = null, CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            text = await GatedDownload.GetStringAsync(url ?? ParametricEqUrl(entry),
                FetchTimeout, MaxPresetBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The timeout surfaces as TaskCanceledException — it must not escape the UI's
            // async void import handler as-is (that would crash the process).
            throw new InvalidOperationException($"Download failed: {ex.Message}", ex);
        }
        var name = PresetStore.SanitizeName(entry.Name);
        var preset = EqPreset.Parse(name, text);
        if (preset.Bands.Count == 0)
            throw new InvalidOperationException(
                "The downloaded file is not an Equalizer APO ParametricEQ file.");
        PresetStore.Save(presetsRoot, name, text);
        return preset;
    }

    private static string? TryReadCache(string cachePath)
    {
        try
        {
            return File.Exists(cachePath) ? File.ReadAllText(cachePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
