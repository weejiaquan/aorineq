namespace AorinEQ.Core.Raop;

/// <summary>An immutable snapshot of what the session is actually doing.
///
/// This exists to be shown to the user. Every comparable product presents AirPlay as a switch
/// that either works or does not; the counters here — retransmit requests in particular — are
/// what turn "real-time versus buffered" from a mystery toggle into a visible trade.</summary>
public sealed record RaopDiagnostics(
    string State,
    string Receiver,
    string ServerName,
    string Codec,
    int SampleRate,
    int FramesPerPacket,
    int QueueMs,
    int ReceiverLatencySamples,
    long PacketsSent,
    long BytesSent,
    double Kbps,
    long RetransmitRequests,
    long RetransmitsServed,
    long RetransmitsMissed,
    long TimingReplies,
    long SyncsSent,
    long SilentPackets,
    string? LastError)
{
    public static RaopDiagnostics Idle { get; } = new(
        "idle", "", "", "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>Retransmit requests as a share of packets sent — the number that moves when the
    /// queue depth changes, and the honest measure of whether a mode is viable on this network.</summary>
    public double RetransmitPercent =>
        PacketsSent == 0 ? 0 : RetransmitRequests * 100.0 / PacketsSent;

    /// <summary>One line for the tray tooltip.</summary>
    public string Summary => State switch
    {
        "streaming" => $"{Receiver} — {Codec}, {Kbps:F0} kbps, {RetransmitPercent:F1}% resends",
        "idle" => "Not streaming",
        _ => LastError is { Length: > 0 } e ? $"{State}: {e}" : State,
    };
}
