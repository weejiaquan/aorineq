using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace AorinEQ.Core;

/// <summary>How launching VB-Audio's setup went.</summary>
public enum VirtualCableLaunch
{
    Ran,
    Cancelled,
    Failed,
}

/// <summary>The persisted "installed, waiting for a restart" state after one more start.</summary>
public sealed record VirtualCablePending(bool Pending, int Starts, bool AnnounceReady);

/// <summary>The guided VB-Cable install, minus the UAC prompt (App owns launching the setup).
///
/// Nothing is bundled: VB-Cable is per-copy licensed for redistribution, so the user's machine
/// fetches VB-Audio's own package from VB-Audio's own server, and VB-Audio's own installer runs
/// interactively so the user sees and accepts their licence. Every step that can be done without
/// the user is done here and gated: the package link is taken only from VB-Audio's host, the
/// download goes through <see cref="GatedDownload"/>, the archive is extracted flat through
/// <see cref="ZipEntryName"/>, and the setup must carry an intact Authenticode signature from
/// VB-Audio's publisher — verified on a handle that stays open, and so keeps the file from being
/// replaced, until the caller has launched it.</summary>
public static class VirtualCableInstaller
{
    /// <summary>The newest pack VB-Audio served when this was written (Oct 2024, 1,318,877 bytes;
    /// Pack46-48 return 404). Used when the page has no package link at all.</summary>
    public const string FallbackPackageUrl =
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    /// <summary>The subject CN on VB-Audio's setup signatures (verified on Pack45, 2026-10-18).</summary>
    public const string SignerCommonName = "BUREL VINCENT Entrepreneur individuel";

    /// <summary>The package is ~1.3 MB; ten times the margin is still nothing like a payload.</summary>
    public const long MaxPackageBytes = 10 * 1024 * 1024;

    /// <summary>Starts after an install that did not produce a cable before giving up on it.</summary>
    public const int PendingStartLimit = 3;

    /// <summary>How long to look for the cable after the setup exits before calling it "needs a
    /// restart".</summary>
    public static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DetectPollInterval = TimeSpan.FromMilliseconds(500);
    private const long MaxPageBytes = 2 * 1024 * 1024;
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: UAC declined

    /// <summary>VB-Audio's download host and cable path, https only. Nothing else is a package.</summary>
    private static readonly Regex PackageLink = new(
        @"https://download\.vb-audio\.com/Download_CABLE/VBCABLE_Driver_Pack(?<pack>\d+)\.zip",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The highest-numbered package link on VB-Audio's page, or null when it has none.</summary>
    public static string? PickPackageUrl(string html) =>
        PackageLink.Matches(html)
            .Select(m => (Url: m.Value, Pack: int.TryParse(m.Groups["pack"].Value, out int n) ? n : -1))
            .Where(p => p.Pack >= 0)
            .OrderByDescending(p => p.Pack)
            .Select(p => p.Url)
            .FirstOrDefault();

    /// <summary>The package to download: the page's newest link, or <see cref="FallbackPackageUrl"/>
    /// when the page has none. Throws <see cref="InvalidOperationException"/> when the PAGE cannot
    /// be fetched, whatever the transport said (a refused connection, a body cut short — HttpClient
    /// reports that as an IOException — or a timeout) — the caller then sends the user to the page
    /// itself.</summary>
    public static async Task<string> ResolvePackageUrlAsync(string pageUrl = VirtualCable.VendorPage,
        CancellationToken cancellationToken = default)
    {
        string html;
        try
        {
            html = await GatedDownload.GetStringAsync(pageUrl, PageTimeout, MaxPageBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new InvalidOperationException($"Couldn't reach VB-Audio's page: {ex.Message}", ex);
        }
        return PickPackageUrl(html) ?? FallbackPackageUrl;
    }

    /// <summary>The package through every gate: https, re-checked redirects, zip magic, 10 MB.</summary>
    public static Task DownloadPackageAsync(string url, string destinationPath,
        CancellationToken cancellationToken = default) =>
        GatedDownload.DownloadAsync(url, destinationPath, MaxPackageBytes, GatedDownload.ZipMagic,
            sha256: null, cancellationToken: cancellationToken);

    /// <summary><c>%TEMP%\aorineq-vbcable</c>: every install's work folder is one guid below it.</summary>
    public static string WorkRoot => Path.Combine(Path.GetTempPath(), "aorineq-vbcable");

    /// <summary>A fresh <c>%TEMP%\aorineq-vbcable\&lt;guid&gt;</c>, created.</summary>
    public static string NewWorkFolder()
    {
        var folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Deletes a work folder and everything in it. Best effort, never throws.</summary>
    public static void DeleteWorkFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"VirtualCableInstaller: couldn't delete '{folder}': {ex.Message}");
        }
    }

    /// <summary>Deletes the work folders under <paramref name="root"/> that an install left behind
    /// when AorinEQ was quit or killed mid-flow, before its own cleanup ran. Called at the single
    /// instance's startup, before anything can be clicked, so no install of THIS process is running
    /// - but VB-Audio's elevated setup may still be: quitting AorinEQ does not close its wizard. A
    /// folder with any file that cannot be opened exclusively (the running setup holds its own exe)
    /// is skipped whole, so the .inf/.sys/.cat beside it are not pulled from under the setup; the
    /// next start sweeps it. Best effort: a folder that cannot go stays, and nothing here throws.</summary>
    public static void DeleteStaleWorkFolders(string root)
    {
        string[] folders;
        try
        {
            if (!Directory.Exists(root)) return;
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"VirtualCableInstaller: couldn't list '{root}': {ex.Message}");
            return;
        }
        int swept = 0;
        foreach (var folder in folders)
        {
            if (HasFileInUse(folder))
            {
                Trace.TraceInformation($"VirtualCableInstaller: left '{folder}': a file in it is in use.");
                continue;
            }
            DeleteWorkFolder(folder);
            swept++;
        }
        if (swept > 0)
            Trace.TraceInformation($"VirtualCableInstaller: swept {swept} stale work folder(s) under '{root}'.");
    }

    /// <summary>Whether any file under <paramref name="folder"/> is held open by someone: it cannot
    /// be opened with no sharing at all. A folder that cannot even be listed counts as in use.</summary>
    private static bool HasFileInUse(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Extracts every entry of the package into one folder, by name. EVERY name is checked
    /// before ANY file is written; one unsafe name throws <see cref="InvalidDataException"/> and
    /// leaves the folder as it was.</summary>
    public static void ExtractFlat(string zipPath, string destinationFolder)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var plan = archive.Entries
            .Select(entry => (Entry: entry, Name: ZipEntryName.FlatName(entry.FullName)
                ?? throw new InvalidDataException($"The package contains an unsafe entry: '{entry.FullName}'.")))
            .ToList();
        Directory.CreateDirectory(destinationFolder);
        foreach (var (entry, name) in plan)
            entry.ExtractToFile(Path.Combine(destinationFolder, name), overwrite: false);
    }

    /// <summary>VB-Audio ships one setup per OS bitness; the x64 one also installs the ARM64 driver.</summary>
    public static string SetupFileName(bool is64BitOperatingSystem) =>
        is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";

    /// <summary>The setup opened read-only with no write or delete sharing, returned ONLY when its
    /// signature is intact and its validated signer is VB-Audio's publisher; null otherwise,
    /// including when the file cannot be opened that way (missing, or another handle can write it).
    /// The caller owns the stream and must keep it open until <c>Process.Start</c> has returned, so
    /// the file verified is the file launched.</summary>
    public static FileStream? OpenTrustedSetup(string exePath)
    {
        FileStream file;
        try
        {
            file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"VirtualCableInstaller: couldn't open '{exePath}' for verification: {ex.Message}");
            return null;
        }
        bool trusted = false;
        try
        {
            trusted = string.Equals(Authenticode.VerifiedSignerCommonName(file), SignerCommonName,
                StringComparison.Ordinal);
            return trusted ? file : null;
        }
        finally
        {
            if (!trusted)
                file.Dispose();
        }
    }

    /// <summary>Extracts the package into <paramref name="workFolder"/> and returns the setup to
    /// run, held open by <see cref="OpenTrustedSetup"/> (same ownership rule); null when the archive
    /// is unsafe or unreadable, has no setup, or the setup is not VB-Audio's.</summary>
    public static FileStream? PrepareSetup(string zipPath, string workFolder, bool is64BitOperatingSystem)
    {
        try
        {
            ExtractFlat(zipPath, workFolder);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"VirtualCableInstaller: couldn't extract '{zipPath}': {ex.Message}");
            return null;
        }
        return OpenTrustedSetup(Path.Combine(workFolder, SetupFileName(is64BitOperatingSystem)));
    }

    /// <summary>What a Win32 error from starting the elevated setup means to the user.</summary>
    public static VirtualCableLaunch LaunchOutcome(int nativeErrorCode) =>
        nativeErrorCode == ErrorCancelled ? VirtualCableLaunch.Cancelled : VirtualCableLaunch.Failed;

    /// <summary>Polls for the cable until it appears or <paramref name="timeout"/> passes. A driver
    /// install can take a few seconds to surface its endpoint after the setup exits.</summary>
    public static async Task<RenderEndpoint?> WaitForCableAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (VirtualCable.Find() is { } cable)
                return cable;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                return null;
            await Task.Delay(left < DetectPollInterval ? left : DetectPollInterval, cancellationToken);
        }
    }

    /// <summary>One app start's step of the "restart to finish installing" state: a cable that has
    /// appeared clears it and is announced; otherwise the start is counted, and the
    /// <see cref="PendingStartLimit"/>th start without one gives up (the install evidently failed,
    /// and the install card shows again).</summary>
    public static VirtualCablePending NextPendingState(bool pending, int starts, bool cableFound)
    {
        if (!pending) return new(false, 0, false);
        if (cableFound) return new(false, 0, true);
        int counted = starts + 1;
        return counted >= PendingStartLimit ? new(false, 0, false) : new(true, counted, false);
    }

    /// <summary>The setup ran but the cable has not appeared: Windows needs a restart. The state
    /// <see cref="ResolvePendingAtStart"/> then moves on each start, counted from zero; nothing else
    /// in <paramref name="settings"/> is touched.</summary>
    public static Settings EnterPendingReboot(Settings settings) =>
        settings with { VirtualCablePendingReboot = true, VirtualCablePendingStarts = 0 };

    /// <summary>One app start's step, as the settings to keep: <see cref="NextPendingState"/>
    /// applied to <paramref name="settings"/>' pending fields, nothing else touched, and whether to
    /// announce the cable. With nothing pending the result EQUALS the input (records compare by
    /// value), so the caller can tell there is nothing to save.</summary>
    public static (Settings Settings, bool AnnounceReady) ResolvePendingAtStart(Settings settings, bool cableFound)
    {
        var next = NextPendingState(settings.VirtualCablePendingReboot, settings.VirtualCablePendingStarts, cableFound);
        return (settings with
        {
            VirtualCablePendingReboot = next.Pending,
            VirtualCablePendingStarts = next.Starts,
        }, next.AnnounceReady);
    }
}
