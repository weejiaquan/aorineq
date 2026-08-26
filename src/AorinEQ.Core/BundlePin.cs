using System.Diagnostics;

namespace AorinEQ.Core;

/// <summary>Holds the app's own unpacked native DLLs open, so a temp-file cleaner cannot delete
/// them out from under a running session.
///
/// AorinEQ ships as a self-contained single file published with
/// <c>IncludeNativeLibrariesForSelfExtract</c> (see publish.ps1 — WPF loads wpfgfx_cor3.dll and
/// PresentationNative_cor3.dll through a plain LoadLibrary, which cannot read them out of the
/// bundle, so they have to be on disk). The host unpacks them to
/// <c>%TEMP%\.net\AorinEQ\&lt;bundle-id&gt;</c> and the process loads them from there for as long as
/// it runs. A tray app runs for days; %TEMP% is a folder Windows treats as disposable.
///
/// On 2026-08-25 Storage Sense — enabled by default in Windows 11, daily cadence, "delete
/// temporary files that my apps aren't using" — ran at 01:50:46 and deleted PenImc_cor3.dll. WPF
/// opens that one lazily, on the first Window.Show(), so on a machine with no pen it is the
/// coldest file in the folder and the first a cleaner picks. At 01:53:23 a volume key was pressed,
/// the OSD went to show itself, and the session died on DllNotFoundException. vcruntime140_cor3.dll
/// had gone the same way two days earlier.
///
/// The other three DLLs survived the identical sweep, and the reason is the entire fix: WPF had
/// already mapped them, so they were locked and the cleaner's delete failed. All this class does is
/// give the remaining files the same protection deliberately, instead of leaving it to whichever
/// ones happen to be in use when the cleaner runs.
///
/// Nothing here throws. It is taken during startup, and a startup helper that fails is worse than
/// the crash it prevents; a build with no extraction directory (any ordinary side-by-side
/// deployment, including the test host) simply pins nothing.</summary>
public sealed class BundlePin : IDisposable
{
    /// <summary>The directory the host unpacks into, two levels below its base:
    /// <c>&lt;base&gt;\.net\&lt;app&gt;\&lt;bundle-id&gt;</c>. Matching on that shape rather than on %TEMP%
    /// keeps this working for a user who has redirected DOTNET_BUNDLE_EXTRACT_BASE_DIR.</summary>
    private const string HostExtractionFolderName = ".net";

    private readonly List<FileStream> _handles = new();

    private BundlePin(string? directory) => Directory = directory;

    /// <summary>Where the pinned files live, or null when this build has no extraction directory
    /// and nothing was pinned.</summary>
    public string? Directory { get; }

    /// <summary>How many files are being held. Zero is a valid, expected result — see the class
    /// remarks.</summary>
    public int PinnedCount => _handles.Count;

    /// <summary>Finds the single-file extraction directory this process is running out of, by
    /// looking for a loaded module that sits two levels under a folder named <c>.net</c>. Returns
    /// null when no loaded module has that shape, which is every deployment except a single file
    /// published for self-extract.</summary>
    /// <param name="loadedModulePaths">Full paths of the modules loaded into the process. Empty
    /// entries are ignored — ProcessModule.FileName can be blank for a module the process cannot
    /// query.</param>
    public static string? FindExtractionDirectory(IEnumerable<string> loadedModulePaths)
    {
        foreach (var modulePath in loadedModulePaths)
        {
            if (string.IsNullOrEmpty(modulePath)) continue;

            string? directory;
            try
            {
                directory = Path.GetDirectoryName(modulePath);
            }
            catch (ArgumentException)
            {
                continue; // not a path this process can reason about; not ours either
            }

            if (directory is null) continue;
            var host = System.IO.Directory.GetParent(directory)?.Parent;
            if (host is not null && string.Equals(host.Name, HostExtractionFolderName, StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    /// <summary>Takes a pin on whatever this process unpacked itself into. A no-op returning an
    /// empty pin when there is no such directory.</summary>
    public static BundlePin AcquireForCurrentProcess()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var paths = self.Modules.Cast<ProcessModule>().Select(m => m.FileName ?? "").ToList();
            return Acquire(FindExtractionDirectory(paths));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or PlatformNotSupportedException)
        {
            // The module list is not readable (it can fail while another thread is loading one).
            // Losing the pin costs the protection this class adds; throwing would cost the session.
            return new BundlePin(null);
        }
    }

    /// <summary>Opens every file in <paramref name="directory"/> for reading and keeps the handles
    /// until disposal. Files that cannot be opened — already deleted, or held exclusively by
    /// something else — are skipped, so one lost file never leaves the others unprotected.</summary>
    public static BundlePin Acquire(string? directory)
    {
        var pin = new BundlePin(directory);
        if (directory is null) return pin;

        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or System.Security.SecurityException)
        {
            return pin; // no directory (or no rights to list it): nothing to protect
        }

        foreach (var file in files)
        {
            try
            {
                // FileShare.Read is the whole contract, and both halves of it matter. It does NOT
                // include Delete, which is what makes a cleaner's DeleteFile fail with a sharing
                // violation. It DOES allow other readers, so the loader can still map a file this
                // pin opened before WPF got to it — PenImc_cor3.dll is loaded lazily by the first
                // Window.Show(), long after startup takes the pin, and FileShare.None here would
                // trade Storage Sense's crash for one of our own.
                pin._handles.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // Gone already, or someone else holds it exclusively. Skip it and keep going.
            }
        }

        return pin;
    }

    /// <summary>Releases every handle. Idempotent: OnExit disposes the pin, and so may an earlier
    /// teardown path.</summary>
    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            try { handle.Dispose(); }
            catch (IOException) { } // closing a read handle that is already gone is not worth a throw
        }

        _handles.Clear();
    }
}
