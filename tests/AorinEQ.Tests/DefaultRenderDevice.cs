using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Test-only control over which render endpoint Windows treats as the default, so the
/// device-change notification path can be driven for real instead of described.
///
/// It goes through IPolicyConfig — the undocumented interface the Sound control panel itself uses.
/// The interop itself is the product's (<see cref="DefaultEndpointSwitcher"/>), so the tests drive
/// the same code AirPlay only does.</summary>
internal static class DefaultRenderDevice
{
    /// <summary>The current default render endpoint id, or null when there is no audio device.</summary>
    public static string? Current => AudioEndpoint.GetDefaultRenderEndpointId();

    /// <summary>The active render endpoints, in enumeration order.</summary>
    public static IReadOnlyList<RenderEndpoint> All => AudioEndpoint.GetRenderEndpoints();

    /// <summary>Makes <paramref name="endpointId"/> the default for all three roles, exactly as
    /// picking it in Sound settings would. Throws when the call fails — a test that cannot switch
    /// the device must fail loudly, not quietly assert nothing.</summary>
    public static void SetDefault(string endpointId)
    {
        if (!DefaultEndpointSwitcher.SetDefaultForAllRoles(endpointId))
            throw new InvalidOperationException(
                $"IPolicyConfig.SetDefaultEndpoint('{endpointId}') failed for at least one role");
    }

    /// <summary>Each role back exactly as found — not via the code under test. Every test that can
    /// move a default leaves through this in a finally.</summary>
    public static void PutBack(DefaultRenderRecord original)
    {
        SetDefaultAndSettle(original.Multimedia!);
        if (original.Console is { } console)
            DefaultEndpointSwitcher.SetDefault(console, AudioEndpoint.ERole.Console);
        if (original.Communications is { } comms)
            DefaultEndpointSwitcher.SetDefault(comms, AudioEndpoint.ERole.Communications);
    }

    /// <summary>The endpoint the machine should be switched TO from <paramref name="current"/> —
    /// the first active render endpoint that is not the current default. Null when the machine has
    /// only one, in which case there is no device change to observe.</summary>
    public static RenderEndpoint? Other(string? current) =>
        All.FirstOrDefault(e => e.Id != current);

    /// <summary>Switches the default and does not return until the machine has gone quiet again:
    /// the id has actually changed AND no further device notification has arrived for
    /// <see cref="QuietPeriod"/>.
    ///
    /// Every test here leaves through this. One switch produces SEVERAL notifications, and they
    /// arrive asynchronously, so a test that returns the moment the id flips leaves its own tail
    /// running into whatever executes next — where a notification stamped with the device the
    /// PREVIOUS test was on arrives in the middle of this one's assertions. That is not the
    /// product being wrong; it is one test's exhaust landing in another's measurement.</summary>
    public static void SetDefaultAndSettle(string endpointId)
    {
        using var watcher = new EndpointVolume();
        long lastEventTicks = DateTime.UtcNow.Ticks;
        watcher.DefaultDeviceChanged += () =>
            Interlocked.Exchange(ref lastEventTicks, DateTime.UtcNow.Ticks);

        SetDefault(endpointId);

        var deadline = DateTime.UtcNow + SettleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var quietFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastEventTicks), DateTimeKind.Utc);
            if (Current == endpointId && quietFor >= QuietPeriod)
                return;
            Thread.Sleep(25);
        }
        throw new TimeoutException(
            $"the default device did not settle on '{endpointId}' within {SettleTimeout.TotalSeconds:F0}s "
            + $"(it now reads '{Current}')");
    }

    /// <summary>How long without a device notification counts as settled. Notifications for one
    /// switch land within a few ms of each other (measured), so this is many times the gap.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);
}
