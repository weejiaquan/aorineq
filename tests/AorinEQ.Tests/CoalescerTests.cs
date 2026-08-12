using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class CoalescerTests
{
    private readonly ITestOutputHelper _out;
    public CoalescerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void First_post_runs_without_a_second_post_or_a_flush()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        using var ran = new ManualResetEventSlim(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Post(() => ran.Set());

        // The leading edge runs on a ThreadPool thread, so this is a HANG bound, not a latency
        // bound: what is being asserted is that a single Post runs on its own, without a second
        // Post and without a Flush to push it. It used to poll for 500 ms and assert on that, and
        // a shared CI runner outran it - a wall-clock bound asserted on a box whose thread pool is
        // saturated by the rest of the suite only ever measures the box. The observed latency is
        // printed instead, which is what the bound was really there to show.
        var signalled = ran.Wait(TimeSpan.FromSeconds(30));
        _out.WriteLine($"leading edge ran={signalled} after {sw.ElapsedMilliseconds} ms");
        Assert.True(signalled, "the leading-edge callback never ran");
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

    [Fact]
    public void Throwing_trailing_action_does_not_crash_and_coalescer_keeps_working()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));

        // Leading edge: runs promptly on a pool thread, starts cooldown.
        c.Post(() => _out.WriteLine("leading action ran"));

        // Queued during cooldown; will run as the trailing action and throw.
        c.Post(() => throw new InvalidOperationException("simulated disk-full failure"));

        // If the exception escaped the timer thread, the test process would be killed here.
        Thread.Sleep(200);
        _out.WriteLine("survived trailing-action exception; process still running");

        // Let the coalescer go fully idle, then confirm it still works normally.
        Thread.Sleep(200);
        int ran = 0;
        c.Post(() => ran++);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500 && Volatile.Read(ref ran) != 1)
        {
            Thread.Sleep(5);
        }
        Assert.Equal(1, ran);
        _out.WriteLine($"post-throw recovery post executed after {sw.ElapsedMilliseconds} ms: ran={ran}");
    }

    [Fact]
    public void Dispose_during_pending_trailing_does_not_throw()
    {
        var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        int ran = 0;

        c.Post(() => { ran++; _out.WriteLine("leading action ran"); }); // leading edge
        c.Post(() => { ran++; _out.WriteLine("trailing action ran"); }); // queued as pending trailing

        var ex = Record.Exception(() => c.Dispose());
        Assert.Null(ex);

        // Give any in-flight timer callback a chance to fire; must not throw or crash.
        Thread.Sleep(200);
        _out.WriteLine($"after dispose + sleep: ran={ran}");

        // Post-after-Dispose must be a silent no-op, not throw.
        var postEx = Record.Exception(() => c.Post(() => _out.WriteLine("should not run")));
        Assert.Null(postEx);
    }

    [Fact]
    public void Dispose_flushes_pending_trailing_action()
    {
        // Interval far beyond the test's lifetime: the trailing action can only ever run if
        // Dispose actually flushes it — a timer firing can't produce a false pass.
        var c = new Coalescer(TimeSpan.FromMinutes(10));
        int lastSeen = -1;

        c.Post(() => Volatile.Write(ref lastSeen, 1)); // leading edge, runs promptly
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000 && Volatile.Read(ref lastSeen) != 1)
        {
            Thread.Sleep(5);
        }
        Assert.Equal(1, Volatile.Read(ref lastSeen));

        c.Post(() => Volatile.Write(ref lastSeen, 2)); // pending trailing, 10 minutes away
        c.Dispose();
        _out.WriteLine($"after dispose: lastSeen={lastSeen}");
        Assert.Equal(2, Volatile.Read(ref lastSeen)); // Dispose ran it synchronously
    }

    [Fact]
    public void Flush_runs_pending_synchronously_and_second_flush_is_a_noop()
    {
        using var c = new Coalescer(TimeSpan.FromMinutes(10));
        int runs = 0;
        int lastSeen = -1;

        c.Post(() => { Interlocked.Increment(ref runs); Volatile.Write(ref lastSeen, 1); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000 && Volatile.Read(ref lastSeen) != 1)
        {
            Thread.Sleep(5);
        }

        c.Post(() => { Interlocked.Increment(ref runs); Volatile.Write(ref lastSeen, 2); });
        c.Flush();
        Assert.Equal(2, Volatile.Read(ref lastSeen)); // pending ran on the flushing thread
        Assert.Equal(2, runs);

        c.Flush(); // nothing pending: must neither throw nor re-run anything
        _out.WriteLine($"after double flush: runs={runs}, lastSeen={lastSeen}");
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Flush_waits_for_in_flight_action()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        var started = new ManualResetEventSlim(false);
        int done = 0;

        c.Post(() =>
        {
            started.Set();
            Thread.Sleep(150);
            Volatile.Write(ref done, 1);
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "action never started");

        c.Flush(); // barrier: must block until the in-flight action completes
        _out.WriteLine($"flush returned; done={done}");
        Assert.Equal(1, Volatile.Read(ref done));
    }

    [Fact]
    public void Flush_never_lets_a_stale_action_overwrite_a_newer_one()
    {
        // Regression for the dequeue/run reordering race: the timer thread can dequeue an older
        // action, lose the CPU, and only run it after Flush() has executed a newer one. With a
        // 1 ms interval the timer contends with Flush on every iteration; the sequence stamps
        // must guarantee the newest post's effect survives every interleaving.
        using var c = new Coalescer(TimeSpan.FromMilliseconds(1));
        int last = 0;
        for (int i = 1; i <= 200; i++)
        {
            int v = i;
            c.Post(() => Volatile.Write(ref last, v));
            if (i % 3 == 0) c.Flush();
        }
        c.Flush();
        Assert.Equal(200, Volatile.Read(ref last)); // newest post landed by the time Flush returned
        Thread.Sleep(150); // any straggler timer action must not roll the value back
        _out.WriteLine($"final value after settle: {last}");
        Assert.Equal(200, Volatile.Read(ref last));
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var c = new Coalescer(TimeSpan.FromMilliseconds(50));
        c.Dispose();
        var ex = Record.Exception(() => c.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Leading_and_trailing_actions_never_overlap()
    {
        using var c = new Coalescer(TimeSpan.FromMilliseconds(50));

        int concurrent = 0;
        int maxConcurrent = 0;

        void TrackEnter()
        {
            int now = Interlocked.Increment(ref concurrent);
            int prevMax;
            do
            {
                prevMax = Volatile.Read(ref maxConcurrent);
                if (now <= prevMax) break;
            } while (Interlocked.CompareExchange(ref maxConcurrent, now, prevMax) != prevMax);
        }

        void TrackExit() => Interlocked.Decrement(ref concurrent);

        var leadingDone = new ManualResetEventSlim(false);

        // Leading action now always runs on a pool thread (Post only arms the timer and
        // returns), so no need to farm the Post call itself out to a background thread.
        c.Post(() =>
        {
            TrackEnter();
            try
            {
                Thread.Sleep(150); // longer than the 50ms interval
            }
            finally
            {
                TrackExit();
                leadingDone.Set();
            }
        });

        // Give the leading action a moment to actually start running.
        Thread.Sleep(20);

        var trailingRan = new ManualResetEventSlim(false);
        c.Post(() =>
        {
            TrackEnter();
            try
            {
                _out.WriteLine("trailing action ran");
            }
            finally
            {
                TrackExit();
                trailingRan.Set();
            }
        });

        Assert.True(leadingDone.Wait(TimeSpan.FromSeconds(5)), "leading action did not complete in time");
        Assert.True(trailingRan.Wait(TimeSpan.FromSeconds(5)), "trailing action did not run in time");

        _out.WriteLine($"max observed concurrency = {maxConcurrent}");
        Assert.Equal(1, maxConcurrent);
    }
}
