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
}
