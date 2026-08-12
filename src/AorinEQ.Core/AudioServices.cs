using System.Diagnostics;

namespace AorinEQ.Core;

/// <summary>Restarting the Windows audio stack — the community-verified substitute for a reboot
/// after an APO is registered on a device (Equalizer APO ticket #214).
///
/// An APO is bound when the audio endpoint is BUILT, so a freshly registered one does nothing
/// until AudioEndpointBuilder rebuilds its endpoints. Restarting that service with -Force takes
/// Audiosrv (which depends on it) down and up in the right order; Audiosrv is started explicitly
/// afterwards because -Force can leave a dependent stopped.
///
/// Spelled once here because two callers need it and they need it differently: the setup guide
/// runs from a non-elevated app and has to raise its own prompt, while the endpoint repair is
/// already inside an elevated process and must not raise a second one — a prompt between a write
/// and its revert is exactly how a user ends up stuck half-repaired.</summary>
public static class AudioServices
{
    /// <summary>Restarting the audio stack is not instant; the endpoints are rebuilt after the
    /// service reports started. Generous, because the cost of being early is a false failure that
    /// triggers a revert of a repair that actually worked.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private const string PowerShellArguments =
        "-NoProfile -ExecutionPolicy Bypass -Command "
        + "\"Restart-Service AudioEndpointBuilder -Force -ErrorAction Stop; "
        + "Start-Service Audiosrv -ErrorAction SilentlyContinue\"";

    /// <summary>The FULL path to Windows PowerShell, never the bare name. One of the two callers
    /// is an already-elevated process, and starting a bare "powershell.exe" resolves it through
    /// the search path — which includes the working directory and anything a user-writable install
    /// location can reach. An elevated process must name the binary it means.</summary>
    public static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The helper process to run. <paramref name="elevate"/> false is only correct when
    /// the CALLER is already elevated.</summary>
    public static ProcessStartInfo BuildStartInfo(bool elevate) => new(PowerShellPath, PowerShellArguments)
    {
        UseShellExecute = true,
        Verb = elevate ? "runas" : "",
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    /// <summary>Runs the restart and waits. Returns the helper's exit code, or null when it could
    /// not be started at all (elevation declined, or no powershell). Blocking — callers on a UI
    /// thread must not use this directly.</summary>
    public static int? Restart(bool elevate)
    {
        try
        {
            var proc = Process.Start(BuildStartInfo(elevate));
            if (proc is null) return null;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Restart, then wait for the endpoints to come back. True only when the helper
    /// reported success — the repair treats anything else as "the change could not take effect"
    /// and puts the endpoint back.</summary>
    public static bool RestartAndSettle(bool elevate)
    {
        if (Restart(elevate) != 0) return false;
        Thread.Sleep(SettleDelay);
        return true;
    }
}
