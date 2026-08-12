using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class ScheduledTaskAutostartTests : IDisposable
{
    private readonly string _taskName = "AorinEQTests-" + Guid.NewGuid().ToString("N");
    private readonly ScheduledTaskAutostart _task;
    private readonly ITestOutputHelper _out;

    public ScheduledTaskAutostartTests(ITestOutputHelper output)
    {
        _out = output;
        _task = new ScheduledTaskAutostart(_taskName, highestRunLevel: false, logonTrigger: false);
    }

    public void Dispose()
    {
        try { _task.Disable(); } catch (InvalidOperationException) { }
    }

    private static string QueryTaskXml(string taskName)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{taskName}\" /XML")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var xml = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return xml;
    }

    [Fact]
    public void Enable_creates_task_and_IsEnabled_reflects_it()
    {
        Assert.False(_task.IsEnabled());
        _task.Enable(@"C:\Tools\AorinEQ.exe");
        _out.WriteLine($"created task {_taskName}");
        Assert.True(_task.IsEnabled());
    }

    [Fact]
    public void Enable_creates_task_without_battery_restrictions_or_time_limit()
    {
        _task.Enable(@"C:\Tools\AorinEQ.exe");
        var xml = QueryTaskXml(_taskName);
        _out.WriteLine("registered task XML:\n" + xml);

        // The whole point of /Create /XML over /SC flags: the flag form silently registers
        // "don't start on batteries", "stop on battery switch" and a 72-hour kill timer.
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
    }

    [Fact]
    public void Disable_removes_task_and_is_safe_when_absent()
    {
        _task.Enable(@"C:\Tools\AorinEQ.exe");
        _task.Disable();
        Assert.False(_task.IsEnabled());
        _task.Disable(); // absent: must not throw
        Assert.False(_task.IsEnabled());
    }

    [Fact]
    public void Enable_with_invalid_settings_throws_with_stderr()
    {
        var bad = new ScheduledTaskAutostart(_taskName + "\\/bad*name", highestRunLevel: false, logonTrigger: false);
        var ex = Assert.Throws<InvalidOperationException>(() => bad.Enable(@"C:\Tools\AorinEQ.exe"));
        _out.WriteLine("error surfaced: " + ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Enable_handles_exe_path_with_spaces()
    {
        var taskWithSpaces = new ScheduledTaskAutostart(_taskName + "-spaces", highestRunLevel: false, logonTrigger: false);
        try
        {
            // Enable with a path containing spaces (path need not exist)
            taskWithSpaces.Enable(@"C:\Program Files\Apo Volume\AorinEQ.exe");
            _out.WriteLine($"created task with spaces in path: {_taskName}-spaces");
            Assert.True(taskWithSpaces.IsEnabled());

            var xmlOutput = QueryTaskXml(_taskName + "-spaces");
            _out.WriteLine($"task XML output contains full path: {xmlOutput.Contains(@"C:\Program Files\Apo Volume\AorinEQ.exe")}");
            Assert.Contains(@"C:\Program Files\Apo Volume\AorinEQ.exe", xmlOutput);
        }
        finally
        {
            try { taskWithSpaces.Disable(); } catch { }
        }
    }

    [Fact]
    public void Enable_xml_escapes_special_characters_in_path()
    {
        var taskAmp = new ScheduledTaskAutostart(_taskName + "-amp", highestRunLevel: false, logonTrigger: false);
        try
        {
            // '&' must be escaped in the task XML or schtasks rejects the definition outright.
            taskAmp.Enable(@"C:\Tools & Apps\AorinEQ.exe");
            Assert.True(taskAmp.IsEnabled());
            var xml = QueryTaskXml(_taskName + "-amp");
            _out.WriteLine("registered command: " + xml);
            Assert.Contains("Tools &amp; Apps", xml);
        }
        finally
        {
            try { taskAmp.Disable(); } catch { }
        }
    }

    [Fact]
    public void Enable_rejects_quote_in_path()
    {
        var ex = Assert.Throws<ArgumentException>(() => _task.Enable(@"C:\Program Files\App""WithQuote\app.exe"));
        _out.WriteLine($"correctly rejected quote in path: {ex.Message}");
        Assert.Contains("double-quote", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_rejects_quote_in_task_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ScheduledTaskAutostart("BadTask\"Name"));
        _out.WriteLine($"correctly rejected quote in task name: {ex.Message}");
        Assert.Contains("double-quote", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_rejects_trailing_backslash_in_task_name()
    {
        // `/TN "{name}\"` — a trailing backslash would escape the closing quote and smuggle the
        // rest of the schtasks command line into the task name argument.
        var ex = Assert.Throws<ArgumentException>(() => new ScheduledTaskAutostart("BadTask\\"));
        _out.WriteLine($"correctly rejected trailing backslash: {ex.Message}");
        Assert.Contains("backslash", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
