using System.Collections.Concurrent;
using System.Diagnostics;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Every guarantee <see cref="SerialWorkQueue"/> documents, asserted — these are the
/// properties the COM callback paths depend on, so "it usually works" is not enough: a queue that
/// reorders would apply a stale device's volume, one that dropped would lose a switch, and one that
/// ran after dispose would touch released COM objects during shutdown.</summary>
public class SerialWorkQueueTests
{
    /// <summary>Generous enough that a loaded machine cannot fail a correct implementation, tight
    /// enough that a blocking Post cannot pass (the blocked action below holds for far longer).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _out;
    public SerialWorkQueueTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Work_runs_on_the_worker_thread_not_the_caller()
    {
        using var queue = new SerialWorkQueue("test-off-thread");
        int callerThread = Environment.CurrentManagedThreadId;
        int workThread = 0;
        var ran = new ManualResetEventSlim();

        queue.Post(() => { workThread = Environment.CurrentManagedThreadId; ran.Set(); });

        Assert.True(ran.Wait(Settle), "the action never ran");
        _out.WriteLine($"caller thread {callerThread}, work thread {workThread}");
        Assert.NotEqual(callerThread, workThread);
    }

    /// <summary>The reason the queue exists: the COM callback thread must get out immediately,
    /// however long the work takes.</summary>
    [Fact]
    public void Post_does_not_wait_for_work_already_running()
    {
        using var queue = new SerialWorkQueue("test-non-blocking");
        var block = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        try
        {
            queue.Post(() => { started.Set(); block.Wait(); });
            Assert.True(started.Wait(Settle), "the blocking action never started");

            var sw = Stopwatch.StartNew();
            Assert.True(queue.Post(() => { }));
            sw.Stop();
            _out.WriteLine($"Post returned in {sw.ElapsedMilliseconds} ms while the worker was blocked");
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Post blocked for {sw.ElapsedMilliseconds} ms behind the running action");
        }
        finally
        {
            block.Set();
        }
    }

    [Fact]
    public void Actions_run_in_post_order_and_none_are_lost()
    {
        const int count = 500;
        using var queue = new SerialWorkQueue("test-fifo");
        var order = new ConcurrentQueue<int>();
        var all = new CountdownEvent(count);

        for (int i = 0; i < count; i++)
        {
            int captured = i;
            Assert.True(queue.Post(() => { order.Enqueue(captured); all.Signal(); }));
        }

        Assert.True(all.Wait(Settle), $"only {count - all.CurrentCount}/{count} actions ran");
        var actual = order.ToArray();
        _out.WriteLine($"ran {actual.Length} actions, first 8: [{string.Join(", ", actual.Take(8))}]");
        Assert.Equal(Enumerable.Range(0, count), actual);
    }

    /// <summary>Serialized, not merely ordered: two actions must never overlap, or the work they
    /// guard would need its own lock.</summary>
    [Fact]
    public void Actions_never_run_concurrently()
    {
        using var queue = new SerialWorkQueue("test-serial");
        int inFlight = 0;
        int maxInFlight = 0;
        var all = new CountdownEvent(200);

        for (int i = 0; i < 200; i++)
        {
            queue.Post(() =>
            {
                int now = Interlocked.Increment(ref inFlight);
                maxInFlight = Math.Max(maxInFlight, now);
                Thread.Sleep(1);
                Interlocked.Decrement(ref inFlight);
                all.Signal();
            });
        }

        Assert.True(all.Wait(Settle));
        _out.WriteLine($"peak concurrent actions: {maxInFlight}");
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public void A_throwing_action_does_not_stop_the_queue()
    {
        using var queue = new SerialWorkQueue("test-throwing");
        var after = new ManualResetEventSlim();

        queue.Post(() => throw new InvalidOperationException("boom"));
        queue.Post(after.Set);

        Assert.True(after.Wait(Settle), "the action after a throwing one never ran");
    }

    [Fact]
    public void Post_after_Dispose_is_refused_and_never_runs()
    {
        var queue = new SerialWorkQueue("test-post-after-dispose");
        queue.Dispose();

        bool ran = false;
        bool accepted = queue.Post(() => ran = true);

        Thread.Sleep(200); // give a wrong implementation time to run it
        _out.WriteLine($"Post accepted={accepted} ran={ran}");
        Assert.False(accepted);
        Assert.False(ran);
    }

    /// <summary>Work that was queued but had not started must be discarded, not run: by the time
    /// Dispose returns the owner has torn down the state those actions would touch.</summary>
    [Fact]
    public void Queued_but_unstarted_work_does_not_run_after_Dispose()
    {
        var queue = new SerialWorkQueue("test-discard-on-dispose");
        var block = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        int laterRan = 0;
        try
        {
            queue.Post(() => { started.Set(); block.Wait(); });
            Assert.True(started.Wait(Settle));
            for (int i = 0; i < 10; i++)
            {
                Assert.True(queue.Post(() => Interlocked.Increment(ref laterRan)));
            }

            block.Set();          // the running action finishes...
            queue.Dispose();      // ...and the ten behind it must be discarded
            Thread.Sleep(300);
        }
        finally
        {
            block.Set();
        }

        _out.WriteLine($"queued-behind actions that ran after Dispose: {Volatile.Read(ref laterRan)}");
        Assert.Equal(0, Volatile.Read(ref laterRan));
    }

    /// <summary>Shutdown may not hang on an action stuck inside a COM call that cannot be
    /// cancelled — Dispose gives it a bounded wait and then leaves it to the background thread.</summary>
    [Fact]
    public void Dispose_returns_even_while_an_action_is_stuck()
    {
        var queue = new SerialWorkQueue("test-stuck-action");
        var block = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        try
        {
            queue.Post(() => { started.Set(); block.Wait(); });
            Assert.True(started.Wait(Settle));

            var sw = Stopwatch.StartNew();
            queue.Dispose();
            sw.Stop();
            _out.WriteLine($"Dispose returned in {sw.ElapsedMilliseconds} ms "
                + $"(join timeout {SerialWorkQueue.DisposeJoinTimeout.TotalMilliseconds} ms)");
            Assert.True(sw.Elapsed < SerialWorkQueue.DisposeJoinTimeout + TimeSpan.FromSeconds(3),
                $"Dispose waited {sw.ElapsedMilliseconds} ms on a stuck action");
        }
        finally
        {
            block.Set();
        }
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var queue = new SerialWorkQueue("test-double-dispose");
        queue.Dispose();
        queue.Dispose(); // must not throw or hang
    }

    /// <summary>An owner whose teardown is triggered BY a notification disposes the queue from
    /// inside the worker. Joining the worker to itself cannot succeed, so it must not be attempted:
    /// Dispose has to return AT ONCE, not sit out its join timeout waiting for the very thread that
    /// is calling it. The timing bound is the assertion — without the self-join guard this still
    /// completes, just a whole DisposeJoinTimeout later.</summary>
    [Fact]
    public void Dispose_from_inside_an_action_returns_immediately_instead_of_joining_itself()
    {
        var queue = new SerialWorkQueue("test-self-dispose");
        var done = new ManualResetEventSlim();
        long elapsedMs = -1;

        queue.Post(() =>
        {
            var sw = Stopwatch.StartNew();
            queue.Dispose();
            elapsedMs = sw.ElapsedMilliseconds;
            done.Set();
        });

        Assert.True(done.Wait(Settle), "Dispose called from a posted action deadlocked");
        _out.WriteLine($"self-Dispose returned in {elapsedMs} ms "
            + $"(join timeout {SerialWorkQueue.DisposeJoinTimeout.TotalMilliseconds} ms)");
        Assert.True(elapsedMs < SerialWorkQueue.DisposeJoinTimeout.TotalMilliseconds / 2,
            $"Dispose from inside the worker took {elapsedMs} ms — it waited on its own thread");
    }
}
