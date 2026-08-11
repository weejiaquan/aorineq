using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ApoVolume.Core;

/// <summary>
/// Logon scheduled task autostart via schtasks.exe. Highest run level starts the app
/// elevated without a UAC prompt; creating/deleting a HIGHEST task itself requires elevation.
/// Tasks are created from a full XML definition (/Create /XML) rather than /SC flags: the
/// flag form silently applies Task Scheduler's defaults of "don't start on batteries", "stop
/// when switching to battery" and a 72-hour execution time limit — all wrong for a resident
/// tray app on a laptop.
/// </summary>
public sealed class ScheduledTaskAutostart
{
    private readonly string _taskName;
    private readonly bool _highestRunLevel;
    private readonly bool _logonTrigger;

    /// <param name="logonTrigger">True (default) for the real run-at-logon trigger; false swaps in
    /// a one-shot time trigger in the past, which registers without touching logon behavior —
    /// used by tests so they can exercise real schtasks round-trips safely.</param>
    public ScheduledTaskAutostart(string taskName = "ApoVolume", bool highestRunLevel = true, bool logonTrigger = true)
    {
        if (taskName.Contains('"'))
            throw new ArgumentException("Task name cannot contain double-quote characters.", nameof(taskName));
        if (taskName.EndsWith('\\'))
            // A trailing backslash before the closing quote in `/TN "{name}\"` would escape that
            // quote and smuggle the rest of the command line into the task name argument.
            throw new ArgumentException("Task name cannot end with a backslash.", nameof(taskName));
        _taskName = taskName;
        _highestRunLevel = highestRunLevel;
        _logonTrigger = logonTrigger;
    }

    public bool IsEnabled() => Run("/Query /TN \"" + _taskName + "\"").ExitCode == 0;

    public void Enable(string exePath)
    {
        if (exePath.Contains('"'))
            throw new ArgumentException("Executable path cannot contain double-quote characters.", nameof(exePath));

        // schtasks reads the XML as UTF-16 per its declaration; Encoding.Unicode writes the
        // matching BOM. The temp file lives in %TEMP% with a GUID name, so its path never needs
        // escaping on the command line below.
        var xmlPath = Path.Combine(Path.GetTempPath(), $"apo-volume-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);
            var result = Run($"/Create /F /TN \"{_taskName}\" /XML \"{xmlPath}\"");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to create scheduled task '{_taskName}': {result.Error}".Trim());
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { }
        }
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

    private string BuildTaskXml(string exePath)
    {
        // SID rather than account name: immune to display-name/domain formatting differences.
        var userId = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Cannot determine the current user for the scheduled task.");
        var runLevel = _highestRunLevel ? "HighestAvailable" : "LeastPrivilege";
        var trigger = _logonTrigger
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(userId)}</UserId></LogonTrigger>"
            // A fixed past StartBoundary registers fine and simply never fires again.
            : "<TimeTrigger><Enabled>true</Enabled><StartBoundary>2000-01-01T00:00:00</StartBoundary></TimeTrigger>";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{runLevel}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Error) Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            // Read stdout asynchronously to avoid deadlock while reading stderr synchronously
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            stdoutTask.Wait();
            return (p.ExitCode, error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // schtasks.exe unlaunchable (policy-stripped system): surface through the class's
            // one exception contract instead of escaping an async void UI handler.
            throw new InvalidOperationException($"Couldn't run schtasks.exe: {ex.Message}", ex);
        }
    }
}
