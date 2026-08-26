using System.Reflection;
using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Pins the two timing constants that keep a session alive.
///
/// These are MEASURED values, not preferences. Against a HomePod mini the receiver closes the
/// RTSP control connection and stops requesting timing after roughly 30 seconds of RTSP
/// silence — while continuing to accept UDP audio into a void, so the sender reports a healthy
/// stream and the user hears nothing. That is the bug this guards.
///
/// A future tidy-up that raises the keep-alive interval "because ten seconds seems chatty"
/// would silently reintroduce it, and the failure takes 30+ seconds of listening to notice.
/// So the relationship is asserted rather than left as a comment.</summary>
public class RaopKeepAliveTests
{
    private readonly ITestOutputHelper _out;
    public RaopKeepAliveTests(ITestOutputHelper output) => _out = output;

    /// <summary>The interval at which the receiver was measured to drop an idle session.</summary>
    private const double ObservedDropSeconds = 30.0;

    private static TimeSpan Constant(string name) =>
        (TimeSpan)typeof(RaopSession)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [Fact]
    public void Keep_alive_fires_well_inside_the_receivers_idle_timeout()
    {
        var interval = Constant("KeepAliveInterval");
        _out.WriteLine($"keep-alive every {interval.TotalSeconds}s; receiver drops after "
                     + $"~{ObservedDropSeconds}s of RTSP silence");

        Assert.True(interval.TotalSeconds > 0, "a zero interval would hammer the receiver");
        Assert.True(interval.TotalSeconds <= ObservedDropSeconds / 2,
            $"keep-alive every {interval.TotalSeconds}s is not safely inside a "
            + $"{ObservedDropSeconds}s idle timeout — two consecutive losses would drop the session");
    }

    [Fact]
    public void Timing_silence_watchdog_allows_for_normal_polling_gaps()
    {
        var timeout = Constant("TimingSilenceTimeout");
        _out.WriteLine($"declare the session dead after {timeout.TotalSeconds}s without a timing request");

        // The receiver polls every ~2s while playing; observed gaps reached 3s under load.
        Assert.True(timeout.TotalSeconds >= 10,
            "too tight — a normal polling gap would be reported as a dead session");
        Assert.True(timeout.TotalSeconds <= ObservedDropSeconds,
            "too loose — the user would hear silence before the app admitted anything was wrong");
    }

    [Fact]
    public void A_fresh_session_reports_no_timing_request_yet()
    {
        var device = new AirPlayDevice("AA@Test", "test.local", "127.0.0.1", 7000,
            new Dictionary<string, string>());
        using var session = new RaopSession(device, queueMs: 1000);
        _out.WriteLine($"before connecting: sinceTiming={session.SecondsSinceLastTimingRequest}, "
                     + $"tcpAlive={session.ControlConnectionAlive}, streaming={session.IsStreaming}");

        Assert.Equal(-1, session.SecondsSinceLastTimingRequest);
        Assert.False(session.ControlConnectionAlive);
        Assert.False(session.IsStreaming);
    }
}
