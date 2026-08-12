using System.Net;
using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class UpdateCheckerTests
{
    private readonly ITestOutputHelper _out;
    public UpdateCheckerTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("v1.8.0", "1.8.0")]
    [InlineData("1.8.0", "1.8.0")]     // bare tag without the v prefix
    [InlineData("v1.10.2", "1.10.2")]
    [InlineData("v1.2.3.4", "1.2.3.4")]
    public void ParseVersionTag_accepts_release_tags(string tag, string expected)
    {
        var v = UpdateChecker.ParseVersionTag(tag);
        _out.WriteLine($"{tag} -> {v}");
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.9.0-beta")] // prerelease-style tags are not versions we ship
    [InlineData("vv1.2.3")]
    [InlineData("1")]
    public void ParseVersionTag_rejects_garbage(string? tag)
    {
        var v = UpdateChecker.ParseVersionTag(tag);
        _out.WriteLine($"'{tag}' -> {v?.ToString() ?? "<null>"}");
        Assert.Null(v);
    }

    [Theory]
    [InlineData("1.9.0", "1.8.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.8.0", "1.8.0", false)]     // equal
    [InlineData("1.9.0", "1.9.0.0", false)]   // 3-part remote vs 4-part local assembly version
    [InlineData("1.9.0.0", "1.9.0", false)]   // and the reverse
    [InlineData("1.7.9", "1.8.0", false)]     // downgrade never offered
    [InlineData("1.8.0", "1.9.0.0", false)]
    public void IsNewer_compares_normalized_versions(string remote, string local, bool expected)
    {
        var result = UpdateChecker.IsNewer(Version.Parse(remote), Version.Parse(local));
        _out.WriteLine($"remote {remote} vs local {local} -> {result}");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseLatestRelease_reads_the_real_v180_response()
    {
        var release = UpdateChecker.ParseLatestRelease(GitHubReleaseFixture.LatestV180Json);
        _out.WriteLine($"parsed: {release}");
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 8, 0), release!.Version);
        Assert.Equal("v1.8.0", release.TagName);
        Assert.Equal("https://github.com/weejiaquan/apo-volume/releases/download/v1.8.0/ApoVolume.exe",
            release.ExeUrl);
        Assert.Null(release.Sha256Url); // v1.8.0 predates the sha asset requirement
        Assert.Equal("https://github.com/weejiaquan/apo-volume/releases/tag/v1.8.0", release.HtmlUrl);
        Assert.False(release.Prerelease);
    }

    [Fact]
    public void ParseLatestRelease_finds_the_sha256_asset_when_present()
    {
        var release = UpdateChecker.ParseLatestRelease(GitHubReleaseFixture.WithSha256Asset());
        Assert.NotNull(release);
        Assert.Equal("https://github.com/weejiaquan/apo-volume/releases/download/v1.8.0/ApoVolume.exe.sha256",
            release!.Sha256Url);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": \"garbage-tag\", \"assets\": []}")]
    [InlineData("[1,2,3]")]
    public void ParseLatestRelease_returns_null_on_bad_payloads(string json)
    {
        var release = UpdateChecker.ParseLatestRelease(json);
        _out.WriteLine($"'{json[..Math.Min(json.Length, 30)]}' -> {release?.ToString() ?? "<null>"}");
        Assert.Null(release);
    }

    [Fact]
    public void ParseLatestRelease_rejects_non_https_asset_urls()
    {
        // A poisoned feed must not be able to point the downloader at plain http.
        var json = GitHubReleaseFixture.LatestV180Json.Replace(
            "https://github.com/weejiaquan/apo-volume/releases/download/v1.8.0/ApoVolume.exe",
            "http://evil.example/ApoVolume.exe");
        var release = UpdateChecker.ParseLatestRelease(json);
        Assert.NotNull(release);
        Assert.Null(release!.ExeUrl); // asset ignored -> no update ever offered from it
    }

    [Theory]
    [InlineData("f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587",
        "f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587")] // bare hex
    [InlineData("F82B23B87DE02C5B5D58D57915030CA434B760A06BE2C9611E735FAD58851587",
        "f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587")] // uppercase normalized
    [InlineData("f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587 *ApoVolume.exe",
        "f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587")] // sha256sum binary format
    [InlineData("f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587  ApoVolume.exe\n",
        "f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587")] // text format + newline
    public void ParseSha256Text_accepts_published_formats(string text, string expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseSha256Text(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("abc123")]
    [InlineData("zzzb23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587")]
    public void ParseSha256Text_rejects_garbage(string text)
    {
        Assert.Null(UpdateChecker.ParseSha256Text(text));
    }

    [Fact]
    public async Task CheckAsync_reports_up_to_date_when_local_is_newer()
    {
        // Real HTTP serving the real captured response — the exact "1.9.0-dev vs published
        // v1.8.0" shape the live app sees right after this version ships.
        var (listener, url, serve) = ServeJson(GitHubReleaseFixture.LatestV180Json);
        var result = await UpdateChecker.CheckAsync(new Version(1, 9, 0, 0), url);
        await serve;
        _out.WriteLine($"status={result.Status} latest={result.Release?.Version}");
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal(new Version(1, 8, 0), result.Release!.Version);
        listener.Stop();
    }

    [Fact]
    public async Task CheckAsync_offers_update_when_remote_is_newer_with_both_assets()
    {
        var (listener, url, serve) = ServeJson(GitHubReleaseFixture.WithSha256Asset("v99.0.0"));
        var result = await UpdateChecker.CheckAsync(new Version(1, 9, 0, 0), url);
        await serve;
        _out.WriteLine($"status={result.Status} latest={result.Release?.Version}");
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Release!.ExeUrl);
        Assert.NotNull(result.Release.Sha256Url);
        listener.Stop();
    }

    [Fact]
    public async Task CheckAsync_missing_sha_asset_is_never_an_update()
    {
        // Remote is newer but carries no sha256 asset: the download gates could never pass, so
        // the check must not offer it.
        var json = GitHubReleaseFixture.LatestV180Json.Replace("\"tag_name\": \"v1.8.0\"",
            "\"tag_name\": \"v99.0.0\"");
        var (listener, url, serve) = ServeJson(json);
        var result = await UpdateChecker.CheckAsync(new Version(1, 9, 0, 0), url);
        await serve;
        _out.WriteLine($"status={result.Status}");
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        listener.Stop();
    }

    [Fact]
    public async Task CheckAsync_http_failure_reports_error_not_update()
    {
        var (listener, url, serve) = ServeStatus(500);
        var result = await UpdateChecker.CheckAsync(new Version(1, 9, 0, 0), url);
        await serve;
        _out.WriteLine($"status={result.Status} error={result.Error}");
        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Null(result.Release);
        Assert.NotNull(result.Error);
        listener.Stop();
    }

    [Fact]
    public async Task CheckAsync_unreachable_host_reports_error()
    {
        var result = await UpdateChecker.CheckAsync(new Version(1, 9, 0, 0),
            "http://localhost:1/releases/latest"); // nothing listens on port 1
        _out.WriteLine($"status={result.Status} error={result.Error}");
        Assert.Equal(UpdateStatus.Error, result.Status);
    }

    private static (HttpListener Listener, string Url, Task Serve) ServeJson(string json)
    {
        var (listener, url) = NewListener();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        return (listener, url, serve);
    }

    private static (HttpListener Listener, string Url, Task Serve) ServeStatus(int status)
    {
        var (listener, url) = NewListener();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = status;
            ctx.Response.Close();
        });
        return (listener, url, serve);
    }

    private static (HttpListener Listener, string Url) NewListener()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return (listener, prefix + "releases/latest");
    }
}
