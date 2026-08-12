using System.Net;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real-machine tests (repo convention: no mocks). This dev machine has Equalizer APO
/// installed and active, which the detection tests rely on the same way the schtasks tests rely
/// on a real Task Scheduler.</summary>
public class EapoOnboardingTests
{
    private readonly ITestOutputHelper _out;
    public EapoOnboardingTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void GetInstallPath_finds_the_real_install()
    {
        var path = EapoDetection.GetInstallPath();
        _out.WriteLine("install path: " + (path ?? "<null>"));
        Assert.NotNull(path);
        Assert.True(Directory.Exists(Path.Combine(path!, "config")));
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void GetConfiguratorPath_points_at_an_existing_exe()
    {
        var path = EapoDetection.GetConfiguratorPath();
        _out.WriteLine("configurator: " + (path ?? "<null>"));
        Assert.NotNull(path);
        Assert.EndsWith("Configurator.exe", path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Default_render_endpoint_id_resolves_and_has_a_guid_tail()
    {
        var id = AudioEndpoint.GetDefaultRenderEndpointId();
        _out.WriteLine("endpoint id: " + (id ?? "<null>"));
        Assert.NotNull(id);
        var guid = AudioEndpoint.EndpointGuid(id);
        _out.WriteLine("endpoint guid: " + (guid ?? "<null>"));
        Assert.NotNull(guid);
        Assert.StartsWith("{", guid);
        Assert.True(Guid.TryParse(guid, out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("no-guid-here", null)]
    [InlineData("{0.0.0.00000000}.{9c1af7ff-1234-1234-1234-123456789abc}", "{9c1af7ff-1234-1234-1234-123456789abc}")]
    public void EndpointGuid_extraction(string? id, string? expected)
    {
        Assert.Equal(expected, AudioEndpoint.EndpointGuid(id));
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    // Active means active ON THE DEFAULT DEVICE, so this one needs both.
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Detect_reports_active_on_this_machine()
    {
        // This dev machine runs AorinEQ against a working EAPO on the default device.
        var status = EapoDetection.Detect();
        _out.WriteLine("status: " + status);
        Assert.Equal(EapoStatus.Active, status);
    }

    [Fact]
    public void IsActiveOnEndpoint_is_false_for_unknown_guid_and_null()
    {
        Assert.False(EapoDetection.IsActiveOnEndpoint("{00000000-0000-0000-0000-000000000000}"));
        Assert.False(EapoDetection.IsActiveOnEndpoint(null));
        Assert.False(EapoDetection.IsActiveOnEndpoint(""));
    }

    [Fact]
    public async Task Download_streams_file_and_reports_progress()
    {
        // Real HTTP against an in-process listener — no external network, no mocks.
        var payload = new byte[300_000];
        new Random(42).NextBytes(payload);
        payload[0] = (byte)'M'; payload[1] = (byte)'Z'; // must look like a PE executable
        var prefix = $"http://localhost:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.ContentLength64 = payload.Length;
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });

        var dest = Path.Combine(Path.GetTempPath(), "apo-dl-test-" + Guid.NewGuid().ToString("N") + ".bin");
        var reports = new List<double>();
        try
        {
            await InstallerDownload.DownloadAsync(prefix, dest, new Progress<double>(reports.Add));
            await serve;
            // Progress<T> posts to the pool; give queued reports a beat to land.
            await Task.Delay(200);
            _out.WriteLine($"downloaded {new FileInfo(dest).Length} bytes, {reports.Count} progress reports, last={reports.LastOrDefault()}");
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports.Max(), precision: 5);
        }
        finally
        {
            File.Delete(dest);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Download_http_error_throws_readable_message_and_leaves_no_file()
    {
        var prefix = $"http://localhost:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        });

        var dest = Path.Combine(Path.GetTempPath(), "apo-dl-test-" + Guid.NewGuid().ToString("N") + ".bin");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallerDownload.DownloadAsync(prefix, dest));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("404", ex.Message);
        Assert.False(File.Exists(dest));
        listener.Stop();
    }

    [Fact]
    public async Task Download_rejects_html_content_type_and_non_pe_payloads()
    {
        // Two listeners, two rejection modes: an HTML content-type (the SourceForge
        // interstitial shape) and a non-PE payload with a binary content-type.
        foreach (var (contentType, body, expectFragment) in new[]
        {
            ("text/html", "<html>Your download will start shortly…</html>", "web page"),
            ("application/octet-stream", "definitely not an exe", "not a Windows installer"),
        })
        {
            var prefix = $"http://localhost:{FreePort()}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var serve = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentType = contentType;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });

            var dest = Path.Combine(Path.GetTempPath(), "apo-dl-test-" + Guid.NewGuid().ToString("N") + ".bin");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallerDownload.DownloadAsync(prefix, dest));
            await serve;
            _out.WriteLine($"{contentType}: {ex.Message}");
            Assert.Contains(expectFragment, ex.Message);
            Assert.False(File.Exists(dest)); // rejected file must not linger for Process.Start
            listener.Stop();
        }
    }

    [Fact]
    public async Task ResolveLatestUrl_builds_direct_download_url_from_release_metadata()
    {
        var prefix = $"http://localhost:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var json = "{\"release\": {\"filename\": \"/1.4.2/EqualizerAPO-x64-1.4.2.exe\", \"url\": \"ignored\"}}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var url = await InstallerDownload.ResolveLatestUrlAsync(prefix);
        await serve;
        _out.WriteLine("resolved: " + url);
        Assert.Equal("https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe", url);
        listener.Stop();
    }

    [Fact]
    public async Task ResolveLatestUrl_bad_metadata_throws_readable_message()
    {
        var prefix = $"http://localhost:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"unexpected\": true}");
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallerDownload.ResolveLatestUrlAsync(prefix));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("equalizerapo.com", ex.Message);
        listener.Stop();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
