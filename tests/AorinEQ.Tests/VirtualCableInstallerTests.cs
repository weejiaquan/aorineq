using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The guided VB-Cable install, every step but the UAC prompt itself: finding the newest
/// package on VB-Audio's real page (a saved copy, and live), downloading it through the real gates,
/// extracting it flat, and verifying the setup's signature AND signer on a handle that stays open
/// (so it cannot be swapped before launch) — plus the tampered and wrong-signer cases that must
/// never be run.</summary>
public class VirtualCableInstallerTests
{
    private readonly ITestOutputHelper _out;
    public VirtualCableInstallerTests(ITestOutputHelper output) => _out = output;

    private const string Prefix = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack";

    private static string SavedVendorPage => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "vb-audio-cable-2026-10-18.html"));

    /// <summary>Microsoft-signed (CN=.NET): a VALID signature by the WRONG publisher.</summary>
    private static string CoreLib => typeof(object).Assembly.Location;

    // ---------------------------------------------------------------- picking the package

    [Fact]
    public void PickPackageUrl_on_the_real_vendor_page_picks_the_highest_pack()
    {
        var url = VirtualCableInstaller.PickPackageUrl(SavedVendorPage);
        _out.WriteLine($"saved page ({SavedVendorPage.Length} chars) -> {url}");
        Assert.Equal(Prefix + "45.zip", url);
    }

    [Fact]
    public void PickPackageUrl_ignores_every_link_outside_the_vendor_host_and_path()
    {
        // Review focus: the vendor page changes, or is tampered with. Only VB-Audio's download host
        // and the Download_CABLE path count, over https.
        const string html = """
            <a href="https://download.vb-audio.com.evil.example/Download_CABLE/VBCABLE_Driver_Pack99.zip">x</a>
            <a href="http://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack98.zip">x</a>
            <a href="https://download.vb-audio.com/Download_MAC/VBCABLE_Driver_Pack97.zip">x</a>
            <a href="https://evil.example/Download_CABLE/VBCABLE_Driver_Pack96.zip">x</a>
            <a href="https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip">x</a>
            <a href="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack99999999999.zip">x</a>
            <a href="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack44.zip">real</a>
            """;
        var url = VirtualCableInstaller.PickPackageUrl(html);
        _out.WriteLine($"hostile page -> {url}");
        Assert.Equal(Prefix + "44.zip", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>VB-CABLE moved to a new page</body></html>")]
    [InlineData("<a href=\"https://download.vb-audio.com/Download_MAC/VBCable_MACDriver_Pack108.zip\">mac</a>")]
    public void PickPackageUrl_returns_null_when_there_is_no_package_link(string html)
    {
        var url = VirtualCableInstaller.PickPackageUrl(html);
        _out.WriteLine($"'{html}' -> {url ?? "(null)"}");
        Assert.Null(url);
    }

    [Fact]
    public async Task A_page_without_a_package_link_falls_back_to_the_pinned_pack()
    {
        var (listener, url, serve) = LoopbackHttp.Serve(Encoding.UTF8.GetBytes("<html>redesigned</html>"));
        try
        {
            var resolved = await VirtualCableInstaller.ResolvePackageUrlAsync(url);
            _out.WriteLine($"page with no link -> {resolved}");
            Assert.Equal(VirtualCableInstaller.FallbackPackageUrl, resolved);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_vendor_page_that_errors_is_a_download_failure_not_a_fallback()
    {
        var (listener, url, serve) = LoopbackHttp.Serve(Encoding.UTF8.GetBytes("gone"), statusCode: 404);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => VirtualCableInstaller.ResolvePackageUrlAsync(url));
            _out.WriteLine($"404 page -> {ex.Message}");
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_vendor_page_that_cannot_be_reached_is_a_download_failure()
    {
        // A loopback port nothing listens on: the connection is refused, which HttpClient reports
        // as HttpRequestException. The install flow only knows "the page failed" — so every
        // transport failure must arrive as the same InvalidOperationException a 404 does.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => VirtualCableInstaller.ResolvePackageUrlAsync($"http://localhost:{port}/"));
        _out.WriteLine($"refused connection -> {ex.Message} (inner {ex.InnerException?.GetType().Name})");
        Assert.IsAssignableFrom<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    [Trait(Requires.Key, Requires.Network)]
    public async Task The_live_vendor_page_names_a_package_on_the_vendor_host()
    {
        var url = await VirtualCableInstaller.ResolvePackageUrlAsync();
        _out.WriteLine($"live page -> {url}");
        Assert.StartsWith(Prefix, url);
    }

    // ---------------------------------------------------------------- extraction

    private static string ZipWith(string folder, params string[] entryNames)
    {
        var zip = Path.Combine(folder, "test-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var name in entryNames)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("content of " + name);
        }
        return zip;
    }

    [Fact]
    public void Flat_entries_are_extracted_by_name()
    {
        var work = VirtualCableInstaller.NewWorkFolder();
        var source = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = ZipWith(source, "readme.txt", "vbMmeCable64_win10.inf", "VBCABLE_Setup_x64.exe");
            VirtualCableInstaller.ExtractFlat(zip, work);
            var files = Directory.GetFiles(work).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _out.WriteLine("extracted: " + string.Join(", ", files));
            Assert.Equal(new[] { "readme.txt", "VBCABLE_Setup_x64.exe", "vbMmeCable64_win10.inf" }, files);
            Assert.Equal("content of readme.txt", File.ReadAllText(Path.Combine(work, "readme.txt")));
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
            VirtualCableInstaller.DeleteWorkFolder(source);
        }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/inner.txt")]
    [InlineData("sub\\inner.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("folder/")]
    [InlineData("evil\0.txt")]
    public void One_unsafe_entry_aborts_the_whole_extraction_before_anything_is_written(string hostile)
    {
        var work = VirtualCableInstaller.NewWorkFolder();
        var source = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = ZipWith(source, "readme.txt", hostile, "VBCABLE_Setup_x64.exe");
            var ex = Assert.Throws<InvalidDataException>(() => VirtualCableInstaller.ExtractFlat(zip, work));
            var written = Directory.GetFiles(work, "*", SearchOption.AllDirectories);
            _out.WriteLine($"'{hostile}' -> {ex.Message}; files written: {written.Length}");
            Assert.Empty(written);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(work)!, "evil.txt")));
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
            VirtualCableInstaller.DeleteWorkFolder(source);
        }
    }

    [Fact]
    public void An_archive_that_is_not_a_package_prepares_no_setup()
    {
        var work = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = ZipWith(work, "readme.txt"); // no setup exe inside
            using var setup = VirtualCableInstaller.PrepareSetup(zip, work, is64BitOperatingSystem: true);
            _out.WriteLine($"package without a setup -> {setup?.Name ?? "(null)"}");
            Assert.Null(setup);
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
        }
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("evil\0.txt")] // a name the file system refuses must not surface as an exception
    public void An_archive_with_an_unsafe_entry_prepares_no_setup(string hostile)
    {
        var work = VirtualCableInstaller.NewWorkFolder();
        var source = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = ZipWith(source, "VBCABLE_Setup_x64.exe", hostile);
            using var setup = VirtualCableInstaller.PrepareSetup(zip, work, is64BitOperatingSystem: true);
            var written = Directory.GetFiles(work);
            _out.WriteLine($"package with '{hostile.Replace("\0", "\\0")}' -> {setup?.Name ?? "(null)"}; files written: {written.Length}");
            Assert.Null(setup);
            Assert.Empty(written);
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
            VirtualCableInstaller.DeleteWorkFolder(source);
        }
    }

    [Fact]
    public void A_package_whose_setup_is_not_vb_audios_prepares_no_setup_and_leaves_nothing_held_open()
    {
        // The setup NAME is right, the signature is Microsoft's: extraction succeeds, verification
        // refuses, and the handle opened to verify must be closed again or the work folder could
        // not be deleted.
        var work = VirtualCableInstaller.NewWorkFolder();
        var source = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = Path.Combine(source, "pack.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                archive.CreateEntryFromFile(CoreLib, VirtualCableInstaller.SetupFileName(true));

            using var setup = VirtualCableInstaller.PrepareSetup(zip, work, is64BitOperatingSystem: true);
            _out.WriteLine($"Microsoft-signed file named {VirtualCableInstaller.SetupFileName(true)} -> {setup?.Name ?? "(null)"}");
            Assert.Null(setup);
            Assert.True(File.Exists(Path.Combine(work, VirtualCableInstaller.SetupFileName(true))));
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
            VirtualCableInstaller.DeleteWorkFolder(source);
        }
        _out.WriteLine($"work folder after delete exists: {Directory.Exists(work)}");
        Assert.False(Directory.Exists(work), "a refused setup must not stay open");
    }

    // ---------------------------------------------------------------- verification

    [Fact]
    public void A_validly_signed_file_by_another_publisher_is_not_trusted_and_is_not_left_open()
    {
        var work = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var copy = Path.Combine(work, "other-publisher.exe");
            File.Copy(CoreLib, copy);
            using var setup = VirtualCableInstaller.OpenTrustedSetup(copy);
            _out.WriteLine($"signed by '.NET' -> {setup?.Name ?? "(not trusted)"}");
            Assert.Null(setup);
            File.Delete(copy); // throws if the verification handle leaked
            Assert.False(File.Exists(copy));
            _out.WriteLine("the refused file deletes at once: no handle left behind");
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
        }
    }

    [Fact]
    public void An_unsigned_file_is_not_trusted()
    {
        var own = typeof(VirtualCableInstaller).Assembly.Location;
        using var setup = VirtualCableInstaller.OpenTrustedSetup(own);
        _out.WriteLine($"{own} (unsigned local build) -> {setup?.Name ?? "(not trusted)"}");
        Assert.Null(setup);
    }

    [Fact]
    public void A_missing_setup_is_not_trusted()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aorineq-vbcable-missing-" + Guid.NewGuid().ToString("N") + ".exe");
        using var setup = VirtualCableInstaller.OpenTrustedSetup(missing);
        _out.WriteLine($"{missing} -> {setup?.Name ?? "(not trusted)"}");
        Assert.Null(setup);
    }

    // ---------------------------------------------------------------- the real package

    [Fact]
    [Trait(Requires.Key, Requires.Network)]
    public async Task The_real_package_passes_the_gates_and_only_an_untouched_vb_audio_setup_is_trusted()
    {
        var url = await VirtualCableInstaller.ResolvePackageUrlAsync();
        var work = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var zip = Path.Combine(work, "pack.zip");
            await VirtualCableInstaller.DownloadPackageAsync(url, zip);
            _out.WriteLine($"{url} -> {new FileInfo(zip).Length:N0} bytes");

            byte[] bytes;
            using (var setup64 = VirtualCableInstaller.PrepareSetup(zip, work, is64BitOperatingSystem: true))
            {
                Assert.NotNull(setup64);
                var cn = Authenticode.VerifiedSignerCommonName(setup64);
                _out.WriteLine($"x64 setup -> {setup64.Name}; verified signer '{cn}'");
                Assert.Equal(Path.Combine(work, VirtualCableInstaller.SetupFileName(true)), setup64.Name);
                Assert.Equal("BUREL VINCENT Entrepreneur individuel", cn);
                Assert.Equal(VirtualCableInstaller.SignerCommonName, cn);
                Assert.True(File.Exists(Path.Combine(work, "vbMmeCable64_win10.inf")),
                    "the setup needs the .inf/.sys/.cat beside it");

                // Held open, the verified setup cannot be replaced before it is launched.
                var swap = Assert.Throws<IOException>(() => File.WriteAllBytes(setup64.Name, new byte[] { 0x4D, 0x5A }));
                _out.WriteLine($"overwrite while held -> {swap.GetType().Name}");
                bytes = File.ReadAllBytes(setup64.Name);
            }

            // The guarantee is "the bytes verified are the bytes run". A genuine VB-Audio setup that
            // another handle can still write cannot be given it, so it is refused — and the same
            // file is trusted the moment that handle is gone.
            var path32 = Path.Combine(work, VirtualCableInstaller.SetupFileName(false));
            using (var writer = new FileStream(path32, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            using (var held = VirtualCableInstaller.OpenTrustedSetup(path32))
            {
                _out.WriteLine($"x86 setup while another handle can write it -> {held?.Name ?? "(not trusted)"}");
                Assert.Null(held);
            }
            using (var setup32 = VirtualCableInstaller.OpenTrustedSetup(path32))
            {
                var cn32 = setup32 is null ? null : Authenticode.VerifiedSignerCommonName(setup32);
                _out.WriteLine($"x86 setup after the writer closed -> {setup32?.Name ?? "(not trusted)"}; verified signer '{cn32}'");
                Assert.NotNull(setup32);
                Assert.Equal("BUREL VINCENT Entrepreneur individuel", cn32);
            }

            var tampered = Path.Combine(work, "tampered.exe");
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            using (var setup = VirtualCableInstaller.OpenTrustedSetup(tampered))
            {
                _out.WriteLine($"one flipped byte -> {setup?.Name ?? "(not trusted)"}");
                Assert.Null(setup);
            }
        }
        finally
        {
            VirtualCableInstaller.DeleteWorkFolder(work);
        }
        Assert.False(Directory.Exists(work), "the work folder must not outlive the install");
    }

    [Fact]
    public async Task A_download_server_that_errors_throws_the_readable_failure()
    {
        var (listener, url, serve) = LoopbackHttp.Serve(Encoding.UTF8.GetBytes("nope"), statusCode: 404);
        var work = VirtualCableInstaller.NewWorkFolder();
        try
        {
            var dest = Path.Combine(work, "pack.zip");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => VirtualCableInstaller.DownloadPackageAsync(url, dest));
            _out.WriteLine($"404 download -> {ex.Message}");
            Assert.False(File.Exists(dest));
            await serve;
        }
        finally
        {
            listener.Stop();
            VirtualCableInstaller.DeleteWorkFolder(work);
        }
    }

    // ---------------------------------------------------------------- small rules

    [Fact]
    public void The_setup_matches_the_operating_systems_bitness()
    {
        _out.WriteLine($"64-bit OS -> {VirtualCableInstaller.SetupFileName(true)}; 32-bit -> {VirtualCableInstaller.SetupFileName(false)}");
        Assert.Equal("VBCABLE_Setup_x64.exe", VirtualCableInstaller.SetupFileName(true));
        Assert.Equal("VBCABLE_Setup.exe", VirtualCableInstaller.SetupFileName(false));
    }

    [Theory]
    [InlineData(1223, VirtualCableLaunch.Cancelled)] // ERROR_CANCELLED: the user said No at UAC
    [InlineData(2, VirtualCableLaunch.Failed)]       // ERROR_FILE_NOT_FOUND
    [InlineData(5, VirtualCableLaunch.Failed)]       // ERROR_ACCESS_DENIED
    public void A_declined_elevation_prompt_reads_as_cancelled_and_anything_else_as_failed(
        int nativeError, VirtualCableLaunch expected)
    {
        var outcome = VirtualCableInstaller.LaunchOutcome(nativeError);
        _out.WriteLine($"Win32 error {nativeError} -> {outcome}");
        Assert.Equal(expected, outcome);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public async Task Waiting_for_the_cable_returns_it_at_once_when_present_and_gives_up_on_time_when_not()
    {
        var expected = VirtualCable.Find();
        var watch = Stopwatch.StartNew();
        var got = await VirtualCableInstaller.WaitForCableAsync(TimeSpan.FromSeconds(1));
        watch.Stop();
        _out.WriteLine($"present at start: {expected?.Id ?? "(no)"}; waited {watch.ElapsedMilliseconds} ms -> {got?.Id ?? "(none)"}");
        Assert.Equal(expected?.Id, got?.Id);
        if (expected is null)
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), "gave up before the timeout");
        else
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "waited for a cable that was already there");
    }

    [Theory]
    [InlineData(false, 0, false, false, 0, false)] // nothing pending: nothing to do
    [InlineData(false, 0, true, false, 0, false)]  // a cable that was always there is not news
    [InlineData(true, 0, true, false, 0, true)]    // first start after the reboot: announce
    [InlineData(true, 2, true, false, 0, true)]
    [InlineData(true, 0, false, true, 1, false)]   // still waiting
    [InlineData(true, 1, false, true, 2, false)]
    [InlineData(true, 2, false, false, 0, false)]  // third start without it: the install failed
    public void A_pending_install_clears_on_success_or_after_three_starts(
        bool pending, int starts, bool found, bool expectPending, int expectStarts, bool expectAnnounce)
    {
        var next = VirtualCableInstaller.NextPendingState(pending, starts, found);
        _out.WriteLine($"pending={pending} starts={starts} found={found} -> {next}");
        Assert.Equal(new VirtualCablePending(expectPending, expectStarts, expectAnnounce), next);
    }

    [Theory]
    [InlineData(false, 0, false, false, 0, false)] // nothing pending
    [InlineData(false, 0, true, false, 0, false)]  // a cable that was always there is not news
    [InlineData(true, 0, true, false, 0, true)]    // first start after the reboot: announce
    [InlineData(true, 1, false, true, 2, false)]   // still waiting, one more start counted
    [InlineData(true, 2, false, false, 0, false)]  // third start without it: give up, card shows again
    public void Each_start_moves_a_pending_install_on_and_touches_nothing_else(
        bool pending, int starts, bool found, bool expectPending, int expectStarts, bool expectAnnounce)
    {
        var before = Settings.Default with
        {
            Percent = 37,
            VirtualCablePendingReboot = pending,
            VirtualCablePendingStarts = starts,
            AirPlay = AirPlaySetting.Default with { AirPlayOnly = true },
        };
        var (after, announce) = VirtualCableInstaller.ResolvePendingAtStart(before, found);
        _out.WriteLine($"pending={pending} starts={starts} found={found} -> pending={after.VirtualCablePendingReboot} "
            + $"starts={after.VirtualCablePendingStarts} announce={announce}");
        Assert.Equal(expectPending, after.VirtualCablePendingReboot);
        Assert.Equal(expectStarts, after.VirtualCablePendingStarts);
        Assert.Equal(expectAnnounce, announce);
        Assert.Equal(before with
        {
            VirtualCablePendingReboot = expectPending,
            VirtualCablePendingStarts = expectStarts,
        }, after);
    }

    [Fact]
    public void A_start_with_nothing_pending_hands_back_settings_equal_to_the_ones_it_was_given()
    {
        // App compares and writes nothing in this case — the common one, every start for everyone
        // who never installed VB-Cable through AorinEQ.
        var settings = Settings.Default with { Percent = 37 };
        var (after, announce) = VirtualCableInstaller.ResolvePendingAtStart(settings, cableFound: true);
        _out.WriteLine($"nothing pending -> equal={after == settings} announce={announce}");
        Assert.Equal(settings, after);
        Assert.False(announce);
    }

    [Fact]
    public void Work_folders_are_unique_under_temp_and_deleting_one_removes_everything_in_it()
    {
        var a = VirtualCableInstaller.NewWorkFolder();
        var b = VirtualCableInstaller.NewWorkFolder();
        File.WriteAllText(Path.Combine(a, "pack.zip"), "x");
        _out.WriteLine($"{a}\n{b}");
        Assert.NotEqual(a, b);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "aorineq-vbcable"), a, StringComparison.OrdinalIgnoreCase);
        VirtualCableInstaller.DeleteWorkFolder(a);
        VirtualCableInstaller.DeleteWorkFolder(b);
        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
    }
}
