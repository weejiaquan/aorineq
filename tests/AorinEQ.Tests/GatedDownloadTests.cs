using System.Net;
using System.Security.Cryptography;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real HTTP against in-process listeners (no mocks), like the InstallerDownload tests.
/// GatedDownload allows plain http for loopback only — which is exactly what lets these tests
/// exercise the real client/stream/gate path locally.</summary>
public class GatedDownloadTests
{
    private readonly ITestOutputHelper _out;
    public GatedDownloadTests(ITestOutputHelper output) => _out = output;

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ZipPayload(int size)
    {
        var payload = new byte[size];
        new Random(7).NextBytes(payload);
        payload[0] = (byte)'P'; payload[1] = (byte)'K';
        return payload;
    }

    private static (HttpListener Listener, string Url, Task Serve) Serve(byte[] body, bool chunked = false)
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            if (chunked) ctx.Response.SendChunked = true;
            else ctx.Response.ContentLength64 = body.Length;
            try { await ctx.Response.OutputStream.WriteAsync(body); }
            catch (HttpListenerException) { } // client aborted mid-body (size-cap tests)
            try { ctx.Response.Close(); } catch (HttpListenerException) { }
        });
        return (listener, prefix + "file.bin", serve);
    }

    private static string TempDest() =>
        Path.Combine(Path.GetTempPath(), "apo-gated-test-" + Guid.NewGuid().ToString("N") + ".bin");

    [Fact]
    public async Task Download_accepts_matching_magic_and_sha256()
    {
        var payload = ZipPayload(200_000);
        var (listener, url, serve) = Serve(payload);
        var dest = TempDest();
        try
        {
            await GatedDownload.DownloadAsync(url, dest, 20 * 1024 * 1024,
                GatedDownload.ZipMagic, Sha256Hex(payload));
            await serve;
            _out.WriteLine($"downloaded {new FileInfo(dest).Length} bytes");
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            File.Delete(dest);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Download_without_pin_skips_sha_gate_but_keeps_magic_gate()
    {
        var payload = ZipPayload(1000);
        var (listener, url, serve) = Serve(payload);
        var dest = TempDest();
        try
        {
            await GatedDownload.DownloadAsync(url, dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, null);
            await serve;
            Assert.True(File.Exists(dest));
        }
        finally
        {
            File.Delete(dest);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Download_rejects_sha256_mismatch_and_deletes_staging()
    {
        var payload = ZipPayload(1000);
        var (listener, url, serve) = Serve(payload);
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            url, dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, new string('0', 64)));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
        listener.Stop();
    }

    [Fact]
    public async Task Download_rejects_wrong_magic_and_deletes_staging()
    {
        var payload = new byte[1000]; // zeros: neither PK nor MZ
        var (listener, url, serve) = Serve(payload);
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            url, dest, 20 * 1024 * 1024, GatedDownload.ExeMagic, Sha256Hex(payload)));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.False(File.Exists(dest));
        listener.Stop();
    }

    [Fact]
    public async Task Download_rejects_declared_oversize_before_reading_body()
    {
        var payload = ZipPayload(50_000);
        var (listener, url, serve) = Serve(payload);
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            url, dest, maxBytes: 10_000, GatedDownload.ZipMagic, null));
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
        listener.Stop();
        await Task.WhenAny(serve, Task.Delay(2000)); // server may see the abort
    }

    [Fact]
    public async Task Download_rejects_streamed_oversize_without_content_length()
    {
        var payload = ZipPayload(200_000);
        var (listener, url, serve) = Serve(payload, chunked: true); // no Content-Length header
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            url, dest, maxBytes: 10_000, GatedDownload.ZipMagic, null));
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
        listener.Stop();
        await Task.WhenAny(serve, Task.Delay(2000));
    }

    [Fact]
    public async Task Download_rejects_plain_http_to_non_loopback_hosts_without_connecting()
    {
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            "http://example.com/skin.zip", dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, null));
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_rejects_a_redirect_to_a_non_https_location()
    {
        // The gate must survive redirects: an allowed-scheme URL that 302s to http://evil is a
        // real bypass if HttpClient's auto-redirect is left on. This listener answers the first
        // request with a 302 to plain http on a real (non-loopback) host.
        var (listener, url) = NewListenerPair();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = "http://example.com/evil.zip";
            ctx.Response.Close();
        });
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            url, dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, null));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("non-https", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
        listener.Stop();
    }

    [Fact]
    public async Task Download_follows_a_redirect_to_an_allowed_location()
    {
        // Legitimate GitHub asset URLs 302 to objects.githubusercontent.com (https). Two
        // listeners: the first redirects to the second (both loopback, both allowed).
        var payload = ZipPayload(50_000);
        var (target, targetUrl) = NewListenerPair();
        var serveTarget = Task.Run(async () =>
        {
            var ctx = await target.GetContextAsync();
            ctx.Response.ContentLength64 = payload.Length;
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });
        var (front, frontUrl) = NewListenerPair();
        var serveFront = Task.Run(async () =>
        {
            var ctx = await front.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = targetUrl;
            ctx.Response.Close();
        });
        var dest = TempDest();
        try
        {
            await GatedDownload.DownloadAsync(frontUrl, dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, null);
            await Task.WhenAll(serveFront, serveTarget);
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            File.Delete(dest);
            front.Stop();
            target.Stop();
        }
    }

    private static (HttpListener Listener, string Url) NewListenerPair()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return (listener, prefix + "file.bin");
    }

    [Fact]
    public async Task Download_http_error_status_throws_readable_message()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var prefix = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        });
        var dest = TempDest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GatedDownload.DownloadAsync(
            prefix, dest, 20 * 1024 * 1024, GatedDownload.ZipMagic, null));
        await serve;
        _out.WriteLine("error: " + ex.Message);
        Assert.Contains("404", ex.Message);
        Assert.False(File.Exists(dest));
        listener.Stop();
    }
}
