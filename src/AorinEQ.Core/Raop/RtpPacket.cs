using System.Buffers.Binary;

namespace AorinEQ.Core.Raop;

/// <summary>The four RAOP wire formats, as pure span writers.
///
/// Every field here is big-endian and every offset is fixed. A receiver handed a packet with a
/// field in the wrong place does not report an error — it plays silence or noise — so these
/// layouts are pinned by unit tests against the bytes a working sender puts on the wire.</summary>
public static class RtpPacket
{
    /// <summary>Standard RTP header length: V/PT, sequence, timestamp, SSRC.</summary>
    public const int HeaderBytes = 12;

    public const byte PayloadTypeAudio = 96;      // 0x60, the dynamic type named in the SDP
    private const byte PayloadTypeSync = 0x54;    // 84
    private const byte PayloadTypeTimingReply = 0x53; // 83
    private const byte PayloadTypeResend = 85;
    private const byte Marker = 0x80;
    private const byte VersionTwo = 0x80;

    public const int SyncBytes = 20;
    public const int TimingReplyBytes = 32;

    /// <summary>Writes the 12-byte RTP header for one audio packet. <paramref name="first"/>
    /// sets the marker bit, which tells the receiver this is the start of a stream.</summary>
    public static int WriteAudioHeader(Span<byte> dest, ushort seq, uint rtpTime, uint ssrc,
        bool first)
    {
        if (dest.Length < HeaderBytes)
            throw new ArgumentException($"need {HeaderBytes} bytes for an RTP header", nameof(dest));
        dest[0] = VersionTwo;
        dest[1] = (byte)(first ? Marker | PayloadTypeAudio : PayloadTypeAudio);
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], rtpTime);
        BinaryPrimitives.WriteUInt32BigEndian(dest[8..], ssrc);
        return HeaderBytes;
    }

    /// <summary>Writes a sync packet for the control channel: the mapping between the sender's
    /// RTP clock and wall time, plus the RTP time the receiver should currently be PLAYING.
    ///
    /// The gap between <paramref name="rtpPlay"/> and <paramref name="rtpNow"/> IS the queue
    /// depth — it is what "realtime" versus "buffered" actually changes.</summary>
    public static int WriteSync(Span<byte> dest, uint rtpNow, uint rtpPlay, ulong ntp, bool first)
    {
        if (dest.Length < SyncBytes)
            throw new ArgumentException($"need {SyncBytes} bytes for a sync packet", nameof(dest));
        dest[0] = (byte)(first ? 0x90 : VersionTwo);
        dest[1] = Marker | PayloadTypeSync;
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..], 7); // length in 32-bit words, minus one
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], rtpPlay);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], ntp);
        BinaryPrimitives.WriteUInt32BigEndian(dest[16..], rtpNow);
        return SyncBytes;
    }

    /// <summary>Answers a timing request. The receiver sends these DURING SETUP and will not
    /// complete the handshake until they are answered — see RaopSession for why that matters.
    ///
    /// The request's own transmit timestamp (its bytes 24..31) must come back verbatim as the
    /// origin field, or the receiver cannot compute the round trip. Returns 0 without writing
    /// anything when the request is too short to carry one.</summary>
    public static int WriteTimingReply(Span<byte> dest, ReadOnlySpan<byte> request,
        ulong received, ulong transmit)
    {
        if (dest.Length < TimingReplyBytes)
            throw new ArgumentException($"need {TimingReplyBytes} bytes for a timing reply", nameof(dest));
        if (request.Length < TimingReplyBytes)
            return 0;
        dest[..TimingReplyBytes].Clear();
        dest[0] = VersionTwo;
        dest[1] = Marker | PayloadTypeTimingReply;
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..], 7);
        // dest[4..8] stays zero
        request.Slice(24, 8).CopyTo(dest[8..]);   // origin: echo the request's transmit time
        BinaryPrimitives.WriteUInt64BigEndian(dest[16..], received);
        BinaryPrimitives.WriteUInt64BigEndian(dest[24..], transmit);
        return TimingReplyBytes;
    }

    /// <summary>True when this control-channel packet is the receiver asking for audio it
    /// missed. The marker bit is masked off: it is set on some receivers and not others, and
    /// only the payload type identifies the packet.</summary>
    public static bool IsRetransmitRequest(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && (packet[1] & 0x7f) == PayloadTypeResend;

    /// <summary>Reads which packets the receiver wants back: a starting sequence number and a
    /// count. False when the packet is not a resend request or is truncated.</summary>
    public static bool ParseRetransmitRequest(ReadOnlySpan<byte> packet, out ushort seq,
        out ushort count)
    {
        seq = 0;
        count = 0;
        if (!IsRetransmitRequest(packet) || packet.Length < 8)
            return false;
        seq = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        count = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        return true;
    }
}
