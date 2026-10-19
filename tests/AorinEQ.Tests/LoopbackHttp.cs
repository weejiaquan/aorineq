using System.Net;

namespace AorinEQ.Tests;

/// <summary>A one-request HTTP server on loopback — real sockets, real HttpListener, no mocks.
/// GatedDownload allows plain http for loopback only, which is what lets the download and
/// page-fetch paths be exercised end to end on the desk.</summary>
internal static class LoopbackHttp
{
    public static (HttpListener Listener, string Url, Task Serve) Serve(
        byte[] body, bool chunked = false, int statusCode = 200)
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
            ctx.Response.StatusCode = statusCode;
            if (chunked) ctx.Response.SendChunked = true;
            else ctx.Response.ContentLength64 = body.Length;
            try { await ctx.Response.OutputStream.WriteAsync(body); }
            catch (HttpListenerException) { } // client aborted mid-body (size-cap tests)
            try { ctx.Response.Close(); } catch (HttpListenerException) { }
        });
        return (listener, prefix + "file.bin", serve);
    }
}
