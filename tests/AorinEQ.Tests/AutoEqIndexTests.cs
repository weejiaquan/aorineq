using System.Net;
using System.Text;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class AutoEqIndexTests
{
    /// <summary>REAL lines captured 2026-08-12 from
    /// https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/INDEX.md —
    /// including the header, "by X on Y" targets, parentheses, '&amp;' sources, and a
    /// non-ASCII model name, exactly as published.</summary>
    private const string RealIndexExcerpt = """
        # Index
        This is a list of all equalization profiles. Target is in parentheses if there are results with multiple targets
        from the same source.

        - [1Custom SA02](./crinacle/711%20in-ear/1Custom%20SA02) by crinacle on 711
        - [Apple AirPods Pro](./HypetheSonics/Bruel%20&%20Kjaer%205128%20in-ear/Apple%20AirPods%20Pro) by HypetheSonics on Bruel & Kjaer 5128
        - [Apple AirPods Pro 2 (51dB + ANC)](./crinacle/711%20in-ear/Apple%20AirPods%20Pro%202%20(51dB%20+%20ANC)) by crinacle on 711
        - [Sennheiser HD 650](./oratory1990/over-ear/Sennheiser%20HD%20650) by oratory1990
        - [Sennheiser HD 650 (2020)](./crinacle/GRAS%2043AG-7%20over-ear/Sennheiser%20HD%20650%20(2020)) by crinacle on GRAS 43AG-7
        - [écoute Audio TH1 (passive)](./Kuulokenurkka/over-ear/%C3%A9coute%20Audio%20TH1%20(passive)) by Kuulokenurkka
        """;

    private readonly ITestOutputHelper _out;
    public AutoEqIndexTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ParseIndex_reads_names_sources_and_paths_from_the_real_format()
    {
        var entries = AutoEqIndex.ParseIndex(RealIndexExcerpt);
        foreach (var e in entries) _out.WriteLine($"{e.Name} | {e.Source} | {e.RelativePath}");
        Assert.Equal(6, entries.Count);
        var hd650 = entries.Single(e => e.Name == "Sennheiser HD 650" && e.Source == "oratory1990");
        Assert.Equal("oratory1990/over-ear/Sennheiser%20HD%20650", hd650.RelativePath);
        // Parenthesized model names must survive the markdown-link parse intact.
        Assert.Contains(entries, e => e.Name == "Apple AirPods Pro 2 (51dB + ANC)");
        Assert.Contains(entries, e => e.Name == "écoute Audio TH1 (passive)");
        Assert.Equal("HypetheSonics", entries.Single(e => e.Name == "Apple AirPods Pro").Source);
    }

    [Fact]
    public void ParseIndex_skips_headers_prose_and_junk()
    {
        var entries = AutoEqIndex.ParseIndex("# Index\nsome prose\n\n- not a link line\n- [x](nohref");
        Assert.Empty(entries);
    }

    [Fact]
    public void ParametricEqUrl_builds_the_raw_github_file_url()
    {
        var entries = AutoEqIndex.ParseIndex(RealIndexExcerpt);
        var hd650 = entries.Single(e => e.Name == "Sennheiser HD 650" && e.Source == "oratory1990");
        var url = AutoEqIndex.ParametricEqUrl(hd650);
        _out.WriteLine(url);
        Assert.Equal(
            "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/"
            + "oratory1990/over-ear/Sennheiser%20HD%20650/Sennheiser%20HD%20650%20ParametricEQ.txt",
            url);
    }

    [Fact]
    public void Search_matches_all_words_against_name_and_source()
    {
        var entries = AutoEqIndex.ParseIndex(RealIndexExcerpt);
        var hits = AutoEqIndex.Search(entries, "hd 650 oratory", 50);
        var hit = Assert.Single(hits);
        Assert.Equal("oratory1990", hit.Source);

        Assert.Equal(2, AutoEqIndex.Search(entries, "sennheiser", 50).Count);
        Assert.Equal(2, AutoEqIndex.Search(entries, "airpods PRO", 50).Count);
        Assert.Single(AutoEqIndex.Search(entries, "airpods pro", 1)); // limit respected
        Assert.Empty(AutoEqIndex.Search(entries, "does-not-exist", 50));
        // Empty query lists everything (the dialog's initial state).
        Assert.Equal(entries.Count, AutoEqIndex.Search(entries, "  ", 50).Count);
    }

    private static (HttpListener Listener, string Url, Func<int> Hits) ServeText(string body)
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        int hits = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; } // listener stopped
                Interlocked.Increment(ref hits);
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                try { await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close(); }
                catch (HttpListenerException) { }
            }
        });
        return (listener, prefix + "index.md", () => hits);
    }

    [Fact]
    public async Task FetchIndex_caches_and_refreshes_on_demand()
    {
        var (listener, url, hits) = ServeText(RealIndexExcerpt);
        var cache = Path.Combine(Path.GetTempPath(), "apo-autoeq-test-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var first = await AutoEqIndex.FetchIndexAsync(cache, refresh: false, url);
            Assert.Equal(RealIndexExcerpt, first);
            Assert.True(File.Exists(cache));
            Assert.Equal(1, hits());

            var second = await AutoEqIndex.FetchIndexAsync(cache, refresh: false, url);
            _out.WriteLine($"hits after cached read: {hits()}");
            Assert.Equal(RealIndexExcerpt, second);
            Assert.Equal(1, hits()); // served from cache, no network

            await AutoEqIndex.FetchIndexAsync(cache, refresh: true, url);
            Assert.Equal(2, hits()); // explicit refresh refetches
        }
        finally
        {
            listener.Stop();
            File.Delete(cache);
        }
    }

    [Fact]
    public async Task FetchIndex_falls_back_to_cache_when_the_fetch_fails()
    {
        var cache = Path.Combine(Path.GetTempPath(), "apo-autoeq-test-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            await File.WriteAllTextAsync(cache, RealIndexExcerpt);
            // Port from a just-closed listener: connection refused, no server.
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            var text = await AutoEqIndex.FetchIndexAsync(cache, refresh: true, $"http://localhost:{port}/index.md");
            _out.WriteLine("fell back to cache");
            Assert.Equal(RealIndexExcerpt, text);
        }
        finally
        {
            File.Delete(cache);
        }
    }

    [Fact]
    public async Task DownloadPreset_saves_the_raw_parametric_file_and_returns_the_preset()
    {
        var (listener, url, _) = ServeText(AutoEqFixture.Hd650ParametricEq);
        var root = Path.Combine(Path.GetTempPath(), "apo-presets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entry = new AutoEqEntry("Sennheiser HD 650", "oratory1990", "x");
            var preset = await AutoEqIndex.DownloadPresetAsync(entry, root, url);
            _out.WriteLine($"preset: {preset.Name}, {preset.Bands.Count} bands, preamp {preset.PreampDb}");
            Assert.Equal(10, preset.Bands.Count);
            Assert.Equal(-6.1, preset.PreampDb, 3);
            // The saved file IS the repo file, byte for byte — import is file copy.
            var saved = await File.ReadAllTextAsync(Path.Combine(root, "Sennheiser HD 650.txt"));
            Assert.Equal(AutoEqFixture.Hd650ParametricEq, saved);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadPreset_rejects_content_that_is_not_a_parametric_eq_file()
    {
        var (listener, url, _) = ServeText("<html>sourceforge interstitial says hi</html>");
        var root = Path.Combine(Path.GetTempPath(), "apo-presets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entry = new AutoEqEntry("Bogus", "x", "x");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => AutoEqIndex.DownloadPresetAsync(entry, root, url));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }
}
