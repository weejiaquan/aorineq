using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The app ships as a self-contained single file published with
/// <c>IncludeNativeLibrariesForSelfExtract</c>, so WPF's five native DLLs are unpacked into
/// %TEMP%\.net\AorinEQ\&lt;id&gt; and loaded from there for the whole life of the process. %TEMP% is,
/// by Windows' own contract, disposable — and Storage Sense is on by default in Windows 11.
///
/// On 2026-08-25 Storage Sense ran at 01:50:46 and deleted PenImc_cor3.dll, the one file WPF had
/// not loaded yet (it is opened lazily by the first Window.Show()). At 01:53:23 a volume key was
/// pressed, the OSD tried to show, and the app died with DllNotFoundException. The three DLLs WPF
/// had already mapped survived the same sweep — a mapped file is locked, and the cleaner skipped
/// them. That is the whole fix: hold every file in that directory open, so the cleaner fails on
/// all five instead of only the three that happened to be in use.</summary>
public class BundlePinTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "aorineq-bundlepin-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public BundlePinTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string content = "not really a dll")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- locating the extraction directory -------------------------------------------------

    [Fact]
    public void FindExtractionDirectory_returns_the_directory_a_bundled_native_module_was_loaded_from()
    {
        var extracted = @"C:\Users\someone\AppData\Local\Temp\.net\AorinEQ\rraCuns5anED";
        var modules = new[]
        {
            @"C:\Users\someone\Downloads\AorinEQ.exe",
            @"C:\Windows\System32\ntdll.dll",
            Path.Combine(extracted, "wpfgfx_cor3.dll"),
        };

        Assert.Equal(extracted, BundlePin.FindExtractionDirectory(modules));
    }

    [Fact]
    public void FindExtractionDirectory_honours_a_redirected_extract_base_dir()
    {
        // DOTNET_BUNDLE_EXTRACT_BASE_DIR moves the base, but the .net\<app>\<id> shape below it is
        // the host's and does not change. Matching on the shape rather than on %TEMP% means a user
        // who has redirected it is still protected.
        var extracted = @"D:\dotnet-bundles\.net\AorinEQ\rraCuns5anED";
        var found = BundlePin.FindExtractionDirectory(new[] { Path.Combine(extracted, "PenImc_cor3.dll") });

        Assert.Equal(extracted, found);
    }

    [Fact]
    public void FindExtractionDirectory_returns_null_for_an_ordinary_side_by_side_deployment()
    {
        // A framework-dependent or non-single-file build loads its natives from beside the exe.
        // Nothing is in %TEMP%, so there is nothing to protect and nothing to pin.
        var modules = new[]
        {
            @"C:\Program Files\AorinEQ\AorinEQ.exe",
            @"C:\Program Files\AorinEQ\wpfgfx_cor3.dll",
            @"C:\Windows\System32\kernel32.dll",
        };

        Assert.Null(BundlePin.FindExtractionDirectory(modules));
    }

    [Fact]
    public void FindExtractionDirectory_ignores_a_dotnet_install_that_merely_has_net_in_the_path()
    {
        // The grandparent must be literally ".net" — two levels up from the file. A path that
        // merely contains "dotnet" or a ".net" segment somewhere else must not be mistaken for
        // the extraction directory, or the app would pin files it does not own.
        var modules = new[]
        {
            @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.30\hostpolicy.dll",
            @"C:\Users\someone\.nuget\packages\something\1.0.0\lib\native.dll",
        };

        Assert.Null(BundlePin.FindExtractionDirectory(modules));
    }

    [Fact]
    public void FindExtractionDirectory_tolerates_a_module_with_no_path()
    {
        // ProcessModule.FileName can be empty for a module the process cannot query.
        var extracted = @"C:\t\.net\AorinEQ\abc";
        var modules = new[] { "", Path.Combine(extracted, "wpfgfx_cor3.dll") };

        Assert.Equal(extracted, BundlePin.FindExtractionDirectory(modules));
    }

    // ---- the pin itself: the actual Storage Sense scenario ---------------------------------

    [Fact]
    public void An_unpinned_file_can_be_deleted_out_from_under_the_process()
    {
        // The bug, reproduced. This is what happened to PenImc_cor3.dll at 01:50:46.
        var dll = WriteFile("PenImc_cor3.dll");

        File.Delete(dll);

        Assert.False(File.Exists(dll));
        _out.WriteLine("unpinned: a cleaner deleted the DLL the app had not loaded yet");
    }

    [Fact]
    public void A_pinned_file_cannot_be_deleted_by_a_cleaner()
    {
        var dll = WriteFile("PenImc_cor3.dll");

        using var pin = BundlePin.Acquire(_dir);

        var ex = Assert.Throws<IOException>(() => File.Delete(dll));
        _out.WriteLine($"pinned: delete refused with {ex.GetType().Name}: {ex.Message}");
        Assert.True(File.Exists(dll));
    }

    [Fact]
    public void Acquire_pins_every_file_in_the_directory()
    {
        // All five, not just the ones WPF happens to have mapped. vcruntime140_cor3.dll was taken
        // by an earlier sweep on 2026-08-23 — every file in there is a live dependency.
        foreach (var name in new[]
                 {
                     "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll",
                     "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
                 })
            WriteFile(name);

        using var pin = BundlePin.Acquire(_dir);

        Assert.Equal(5, pin.PinnedCount);
        foreach (var path in Directory.GetFiles(_dir))
            Assert.Throws<IOException>(() => File.Delete(path));
    }

    [Fact]
    public void A_pinned_file_can_still_be_opened_for_reading_so_LoadLibrary_keeps_working()
    {
        // The share mode is the subtle part. FileShare.None would defeat the cleaner AND the
        // loader: PenImc_cor3.dll is opened lazily by the first Window.Show(), long after the pin
        // is taken, and it must still be loadable. FileShare.Read denies deletion and nothing else.
        var dll = WriteFile("PenImc_cor3.dll", "the bytes the loader needs");

        using var pin = BundlePin.Acquire(_dir);

        using var reader = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Assert.Equal("the bytes the loader needs", new StreamReader(reader).ReadToEnd());
    }

    [Fact]
    public void Dispose_releases_every_handle()
    {
        var dll = WriteFile("PenImc_cor3.dll");

        var pin = BundlePin.Acquire(_dir);
        Assert.Throws<IOException>(() => File.Delete(dll));

        pin.Dispose();

        File.Delete(dll); // must not throw — the process is done with it
        Assert.False(File.Exists(dll));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        WriteFile("PenImc_cor3.dll");
        var pin = BundlePin.Acquire(_dir);

        pin.Dispose();
        pin.Dispose(); // OnExit disposes it; so may an earlier teardown path
    }

    // ---- it runs from a crash-adjacent path, so it may never throw -------------------------

    [Fact]
    public void Acquire_on_a_null_directory_pins_nothing_and_does_not_throw()
    {
        using var pin = BundlePin.Acquire(null);

        Assert.Equal(0, pin.PinnedCount);
    }

    [Fact]
    public void Acquire_on_a_missing_directory_pins_nothing_and_does_not_throw()
    {
        using var pin = BundlePin.Acquire(Path.Combine(_dir, "gone"));

        Assert.Equal(0, pin.PinnedCount);
    }

    [Fact]
    public void Acquire_skips_a_file_that_has_already_been_deleted_and_pins_the_rest()
    {
        // The startup race: a sweep can land between process start and the pin being taken. The
        // file is lost for this session either way (the host re-extracts it on the next launch) —
        // what must not happen is the rest going unprotected because of it.
        WriteFile("wpfgfx_cor3.dll");
        var doomed = WriteFile("PenImc_cor3.dll");
        File.Delete(doomed);

        using var pin = BundlePin.Acquire(_dir);

        Assert.Equal(1, pin.PinnedCount);
    }

    [Fact]
    public void Acquire_skips_a_file_held_exclusively_by_something_else_and_pins_the_rest()
    {
        WriteFile("wpfgfx_cor3.dll");
        var contested = WriteFile("PenImc_cor3.dll");
        using var exclusive = new FileStream(contested, FileMode.Open, FileAccess.Read, FileShare.None);

        using var pin = BundlePin.Acquire(_dir);

        Assert.Equal(1, pin.PinnedCount);
    }

    [Fact]
    public void AcquireForCurrentProcess_pins_nothing_when_not_running_as_a_single_file_bundle()
    {
        // The test host is an ordinary side-by-side build, so there is no extraction directory.
        // The contract that matters here is that it is a no-op rather than a throw — this is
        // called during startup, and a startup helper that throws is a worse bug than the one it
        // is fixing.
        using var pin = BundlePin.AcquireForCurrentProcess();

        _out.WriteLine($"pinned {pin.PinnedCount} file(s) from {pin.Directory ?? "(no bundle extraction directory)"}");
        Assert.Null(pin.Directory);
        Assert.Equal(0, pin.PinnedCount);
    }
}
