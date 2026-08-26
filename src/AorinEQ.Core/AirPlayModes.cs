namespace AorinEQ.Core;

/// <summary>How much audio the sender keeps queued ahead of playback.
///
/// The knob is the sync packet's latency field — the gap between the RTP time the sender is
/// stamping and the RTP time it tells the receiver to be playing. A short queue means the audio
/// arrives just in time and a lost packet becomes a gap; a long queue means the receiver has a
/// cushion and a lost packet can be retransmitted before it is needed.
///
/// String constants rather than an enum so the persisted json stays human-readable and unknown
/// values normalize gracefully — the same idiom as <see cref="VolumeModes"/> and
/// <see cref="TrayActions"/>.
///
/// The preset values are PROVISIONAL. The receiver reports an Audio-Latency of 1886 samples
/// (43 ms), which is demonstrably not what it actually buffers, so there is no honest way to
/// calibrate these from the protocol — they need measuring by ear. The retransmit counter shown
/// beside the selector is what makes the trade visible in the meantime.</summary>
public static class AirPlayModes
{
    public const string Realtime = "realtime";
    public const string Normal = "normal";
    public const string Buffered = "buffered";
    public const string Custom = "custom";

    public const int MinQueueMs = 100;
    public const int MaxQueueMs = 5000;

    private const int RealtimeMs = 250;
    private const int NormalMs = 1000;
    private const int BufferedMs = 2000;

    public static readonly IReadOnlyList<string> All = [Realtime, Normal, Buffered, Custom];

    public static bool IsMode(string? mode) => mode is not null && All.Contains(mode);

    public static string Normalize(string? mode, string fallback) =>
        IsMode(mode) ? mode! : fallback;

    /// <summary>The queue depth a mode asks for, in milliseconds. Custom is clamped: a
    /// hand-edited settings file must not be able to request a queue the sender cannot honour.</summary>
    public static int QueueMs(string mode, int customMs) => mode switch
    {
        Realtime => RealtimeMs,
        Buffered => BufferedMs,
        Custom => Math.Clamp(customMs, MinQueueMs, MaxQueueMs),
        _ => NormalMs,
    };

    /// <summary>What to show next to each preset.</summary>
    public static string DisplayName(string mode) => mode switch
    {
        Realtime => "Real-time",
        Buffered => "Buffered",
        Custom => "Custom",
        _ => "Normal",
    };
}

/// <summary>The RAOP receiver's own volume, which is a dB scale rather than a percentage.
///
/// SET_PARAMETER takes -30 dB (quietest audible) through 0 dB (loudest), with -144 as a
/// distinct mute value. Users get a percentage; this is the one place the two meet.</summary>
public static class AirPlayVolume
{
    /// <summary>RAOP's mute. Not simply "very quiet" — receivers treat it specially.</summary>
    public const double MuteDb = -144.0;

    public const double MinDb = -30.0;
    public const double MaxDb = 0.0;

    /// <summary>0% is mute; 1–100% spans -29.7 dB to 0 dB linearly.</summary>
    public static double ToDb(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return percent == 0 ? MuteDb : MinDb + percent * (-MinDb / 100.0);
    }

    /// <summary>The inverse, exact for every value <see cref="ToDb"/> produces.</summary>
    public static int ToPercent(double db)
    {
        // At or below the bottom of the scale — including MuteDb — is 0%. Nothing ToDb produces
        // lands there except mute itself: 1% is -29.7 dB.
        if (db <= MinDb) return 0;
        if (db >= MaxDb) return 100;
        return Math.Clamp((int)Math.Round((db - MinDb) * (100.0 / -MinDb)), 0, 100);
    }
}

/// <summary>One AirPlay target and how to stream to it.
///
/// <paramref name="DeviceId"/> is the mDNS instance name, which carries the receiver's MAC and
/// so survives a rename. <paramref name="SourceEndpointId"/> empty means "follow the default
/// render endpoint"; naming one pins the stream's source, which is how a virtual device gets
/// tapped without the local speakers hearing it.
///
/// <paramref name="AutoRetargetVolume"/> only has meaning in <see cref="VolumeModes.System"/>.
/// In Equalizer APO mode the preamp already sits upstream of the loopback tap, so the volume
/// keys attenuate the stream before it is ever packetised and retargeting would attenuate it
/// twice.</summary>
public sealed record AirPlaySetting(
    bool Enabled = false,
    string DeviceName = "",
    string DeviceId = "",
    string SourceEndpointId = "",
    string Mode = AirPlayModes.Normal,
    int CustomQueueMs = 1000,
    int VolumePercent = 70,
    bool AutoRetargetVolume = true)
{
    public static AirPlaySetting Default { get; } = new();
}
