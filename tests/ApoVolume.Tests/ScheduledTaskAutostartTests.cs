using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ScheduledTaskAutostartTests : IDisposable
{
    private readonly string _taskName = "ApoVolumeTests-" + Guid.NewGuid().ToString("N");
    private readonly ScheduledTaskAutostart _task;
    private readonly ITestOutputHelper _out;

    public ScheduledTaskAutostartTests(ITestOutputHelper output)
    {
        _out = output;
        _task = new ScheduledTaskAutostart(_taskName, highestRunLevel: false, scheduleArgs: "/SC ONCE /ST 00:00");
    }

    public void Dispose()
    {
        try { _task.Disable(); } catch (InvalidOperationException) { }
    }

    [Fact]
    public void Enable_creates_task_and_IsEnabled_reflects_it()
    {
        Assert.False(_task.IsEnabled());
        _task.Enable(@"C:\Tools\ApoVolume.exe");
        _out.WriteLine($"created task {_taskName}");
        Assert.True(_task.IsEnabled());
    }

    [Fact]
    public void Disable_removes_task_and_is_safe_when_absent()
    {
        _task.Enable(@"C:\Tools\ApoVolume.exe");
        _task.Disable();
        Assert.False(_task.IsEnabled());
        _task.Disable(); // absent: must not throw
        Assert.False(_task.IsEnabled());
    }

    [Fact]
    public void Enable_with_invalid_settings_throws_with_stderr()
    {
        var bad = new ScheduledTaskAutostart(_taskName + "\\/bad*name", highestRunLevel: false, scheduleArgs: "/SC ONCE /ST 00:00");
        var ex = Assert.Throws<InvalidOperationException>(() => bad.Enable(@"C:\Tools\ApoVolume.exe"));
        _out.WriteLine("error surfaced: " + ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Enable_handles_exe_path_with_spaces()
    {
        var taskWithSpaces = new ScheduledTaskAutostart(_taskName + "-spaces", highestRunLevel: false, scheduleArgs: "/SC ONCE /ST 00:00");
        try
        {
            // Enable with a path containing spaces (path need not exist)
            taskWithSpaces.Enable(@"C:\Program Files\Apo Volume\ApoVolume.exe");
            _out.WriteLine($"created task with spaces in path: {_taskName}-spaces");
            Assert.True(taskWithSpaces.IsEnabled());

            // Verify task command via schtasks query
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{_taskName}-spaces\" /XML")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var xmlOutput = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _out.WriteLine($"task XML output contains full path: {xmlOutput.Contains(@"C:\Program Files\Apo Volume\ApoVolume.exe")}");
            Assert.Contains(@"C:\Program Files\Apo Volume\ApoVolume.exe", xmlOutput);
        }
        finally
        {
            try { taskWithSpaces.Disable(); } catch { }
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
}
