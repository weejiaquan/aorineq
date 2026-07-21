using System.Diagnostics;

namespace ApoVolume.Core;

/// <summary>
/// Logon scheduled task autostart via schtasks.exe. Highest run level starts the app
/// elevated without a UAC prompt; creating/deleting a HIGHEST task itself requires elevation.
/// </summary>
public sealed class ScheduledTaskAutostart
{
    private readonly string _taskName;
    private readonly bool _highestRunLevel;

    public ScheduledTaskAutostart(string taskName = "ApoVolume", bool highestRunLevel = true)
    {
        _taskName = taskName;
        _highestRunLevel = highestRunLevel;
    }

    public bool IsEnabled() => Run("/Query /TN \"" + _taskName + "\"").ExitCode == 0;

    public void Enable(string exePath)
    {
        var runLevel = _highestRunLevel ? "HIGHEST" : "LIMITED";
        var result = Run($"/Create /F /TN \"{_taskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL {runLevel}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to create scheduled task '{_taskName}': {result.Error}".Trim());
    }

    public void Disable()
    {
        var result = Run("/Delete /F /TN \"" + _taskName + "\"");
        if (result.ExitCode == 0)
            return; // success

        // Exit code 1: check if task is already absent via /Query
        var queryResult = Run("/Query /TN \"" + _taskName + "\"");
        if (queryResult.ExitCode != 0)
            return; // task already absent, no error

        // Task exists but delete failed: surface the error
        throw new InvalidOperationException(
            $"Failed to delete scheduled task '{_taskName}': {result.Error}".Trim());
    }

    private static (int ExitCode, string Error) Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, error);
    }
}
