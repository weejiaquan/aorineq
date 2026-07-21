using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class CoalescerTests
{
    private readonly ITestOutputHelper _out;
    public CoalescerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void First_post_runs_synchronously()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        int ran = 0;
        c.Post(() => ran++);
        Assert.Equal(1, ran); // leading edge: no latency on a single keypress
    }

    [Fact]
    public void Rapid_posts_coalesce_but_last_value_always_lands()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        int executed = 0;
        int lastSeen = -1;
        for (int i = 0; i < 100; i++)
        {
            int v = i;
            c.Post(() => { Interlocked.Increment(ref executed); Volatile.Write(ref lastSeen, v); });
        }
        // wait out the trailing edge generously (real timers, no mocks)
        Thread.Sleep(500);
        _out.WriteLine($"executed {executed} of 100 posts; lastSeen={lastSeen}");
        Assert.True(executed < 100, $"expected coalescing, but all {executed} posts ran");
        Assert.True(executed >= 1, "at least the leading post must run");
        Assert.Equal(99, lastSeen); // the LAST value must be the one that sticks
    }

    [Fact]
    public void Posts_in_separate_idle_periods_all_run()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        int ran = 0;
        c.Post(() => ran++);
        Thread.Sleep(150);
        c.Post(() => ran++);
        Thread.Sleep(150);
        Assert.Equal(2, ran);
    }
}
