using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The thread the app's low-level hooks live on. Every property asserted here is one
/// the hooks depend on: work really does leave the caller's thread (that IS the fix — a hook
/// callback must never queue behind the UI thread), it always lands on the SAME thread (a hook
/// is only ever called on the thread that installed it, so install and uninstall have to agree),
/// the apartment is STA, and the loop can be shut down without stranding a caller.</summary>
public class HookThreadTests
{
    private readonly ITestOutputHelper _out;
    public HookThreadTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Work_runs_on_the_hook_thread_not_the_caller_s()
    {
        using var hooks = new HookThread();
        int caller = Environment.CurrentManagedThreadId;

        int ran = hooks.Invoke(() => Environment.CurrentManagedThreadId);

        _out.WriteLine($"caller thread={caller}, hook thread={ran}");
        Assert.NotEqual(caller, ran);
    }

    [Fact]
    public void Every_invocation_lands_on_the_same_thread()
    {
        using var hooks = new HookThread();

        var ids = Enumerable.Range(0, 20)
            .Select(_ => hooks.Invoke(() => Environment.CurrentManagedThreadId))
            .Distinct()
            .ToList();

        _out.WriteLine($"distinct thread ids across 20 invocations: {string.Join(", ", ids)}");
        Assert.Single(ids);
    }

    [Fact]
    public void The_thread_is_STA_and_background()
    {
        using var hooks = new HookThread();

        var (apartment, background) = hooks.Invoke(
            () => (Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground));

        _out.WriteLine($"apartment={apartment}, background={background}");
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(background, "a foreground hook thread would keep the process alive after exit");
    }

    [Fact]
    public void Invoke_returns_the_work_s_own_value()
    {
        using var hooks = new HookThread();

        Assert.Equal("installed", hooks.Invoke(() => "installed"));
        Assert.Equal(new IntPtr(1234), hooks.Invoke(() => new IntPtr(1234)));
    }

    [Fact]
    public void The_action_overload_runs_the_work_on_the_hook_thread()
    {
        using var hooks = new HookThread();
        int caller = Environment.CurrentManagedThreadId;
        int ran = 0;

        hooks.Invoke(() => ran = Environment.CurrentManagedThreadId);

        _out.WriteLine($"caller thread={caller}, hook thread={ran}");
        Assert.NotEqual(0, ran);
        Assert.NotEqual(caller, ran);
    }

    [Fact]
    public void A_throwing_work_item_rethrows_on_the_caller_and_leaves_the_loop_running()
    {
        using var hooks = new HookThread();

        var ex = Assert.Throws<InvalidOperationException>(
            () => hooks.Invoke<int>(() => throw new InvalidOperationException("hook refused")));
        Assert.Equal("hook refused", ex.Message);

        // The whole point of surviving it: SetWindowsHookEx failing for one hook must not take
        // the thread the other one lives on down with it.
        Assert.Equal(7, hooks.Invoke(() => 7));
    }

    [Fact]
    public void Invoke_from_the_hook_thread_itself_runs_inline_instead_of_deadlocking()
    {
        using var hooks = new HookThread();

        // The outer Invoke occupies the loop; a nested one that queued would wait for a loop that
        // is waiting for it. Inline execution is what makes a re-entrant callback safe.
        var (outer, inner) = hooks.Invoke(() =>
            (Environment.CurrentManagedThreadId, hooks.Invoke(() => Environment.CurrentManagedThreadId)));

        _out.WriteLine($"outer={outer}, nested={inner}");
        Assert.Equal(outer, inner);
    }

    [Fact]
    public void Dispose_stops_the_loop_and_refuses_further_work()
    {
        var hooks = new HookThread();
        int alive = hooks.Invoke(() => Environment.CurrentManagedThreadId);
        _out.WriteLine($"hook thread {alive} ran before dispose");

        hooks.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hooks.Invoke(() => 1));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var hooks = new HookThread();
        hooks.Invoke(() => 1);

        hooks.Dispose();
        hooks.Dispose(); // teardown reaches this by more than one path; the second must be a no-op
    }
}
