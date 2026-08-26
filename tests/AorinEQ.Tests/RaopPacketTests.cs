using System.Buffers.Binary;
using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The RAOP wire formats, byte by byte.
///
/// These layouts are the part of the protocol with no error signal: a receiver handed a packet
/// with a field in the wrong place does not complain, it simply plays nothing or plays noise.
/// So every offset, every marker bit and every endianness choice is pinned here against the
/// values observed on the wire from a sender that works.</summary>
public class RaopPacketTests
{
    private readonly ITestOutputHelper _out;
    public RaopPacketTests(ITestOutputHelper output) => _out = output;

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    // ---- audio header -------------------------------------------------------------------

    [Fact]
    public void Audio_header_is_twelve_bytes_with_version_two()
    {
        var buf = new byte[RtpPacket.HeaderBytes];
        int n = RtpPacket.WriteAudioHeader(buf, seq: 0x1234, rtpTime: 0x89ABCDEF,
            ssrc: 0x11223344, first: false);
        _out.WriteLine($"audio header: {Hex(buf)} ({n} bytes)");

        Assert.Equal(12, n);
        Assert.Equal(0x80, buf[0]);                 // V=2, no padding/extension/CSRC
    }

    [Fact]
    public void Audio_header_sets_the_marker_bit_only_on_the_first_packet()
    {
        var first = new byte[RtpPacket.HeaderBytes];
        var later = new byte[RtpPacket.HeaderBytes];
        RtpPacket.WriteAudioHeader(first, 1, 1, 1, first: true);
        RtpPacket.WriteAudioHeader(later, 1, 1, 1, first: false);
        _out.WriteLine($"first byte1=0x{first[1]:X2}  later byte1=0x{later[1]:X2}");

        Assert.Equal(0x80 | RtpPacket.PayloadTypeAudio, first[1]); // 0xE0
        Assert.Equal(RtpPacket.PayloadTypeAudio, later[1]);        // 0x60
    }

    [Fact]
    public void Audio_header_writes_seq_timestamp_and_ssrc_big_endian()
    {
        var buf = new byte[RtpPacket.HeaderBytes];
        RtpPacket.WriteAudioHeader(buf, seq: 0x1234, rtpTime: 0x89ABCDEF,
            ssrc: 0x11223344, first: false);
        _out.WriteLine($"audio header: {Hex(buf)}");

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2)));
        Assert.Equal(0x89ABCDEFu, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8)));
    }

    // ---- sync packet --------------------------------------------------------------------

    [Fact]
    public void Sync_packet_layout_matches_the_wire_format()
    {
        var buf = new byte[32];
        int n = RtpPacket.WriteSync(buf, rtpNow: 0xAABBCCDD, rtpPlay: 0x11223344,
            ntp: 0x0102030405060708UL, first: false);
        _out.WriteLine($"sync: {Hex(buf.AsSpan(0, n))} ({n} bytes)");

        Assert.Equal(20, n);
        Assert.Equal(0x80, buf[0]);
        Assert.Equal(0xD4, buf[1]);                                            // PT 84 | marker
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2)));  // length in words
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4)));  // play time
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(8)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16))); // now
    }

    [Fact]
    public void Sync_packet_flags_the_first_one()
    {
        var first = new byte[20];
        var later = new byte[20];
        RtpPacket.WriteSync(first, 1, 1, 1, first: true);
        RtpPacket.WriteSync(later, 1, 1, 1, first: false);
        _out.WriteLine($"first byte0=0x{first[0]:X2}  later byte0=0x{later[0]:X2}");

        Assert.Equal(0x90, first[0]);
        Assert.Equal(0x80, later[0]);
    }

    // ---- timing reply -------------------------------------------------------------------

    [Fact]
    public void Timing_reply_echoes_the_requests_transmit_timestamp_as_origin()
    {
        // A timing request carries its own transmit timestamp at bytes 24..31. The reply must
        // return it verbatim as the "origin" field or the receiver cannot compute the offset.
        var request = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), 0xDEADBEEFCAFEF00DUL);

        var reply = new byte[32];
        int n = RtpPacket.WriteTimingReply(reply, request,
            received: 0x1111111122222222UL, transmit: 0x3333333344444444UL);
        _out.WriteLine($"timing reply: {Hex(reply)} ({n} bytes)");

        Assert.Equal(32, n);
        Assert.Equal(0x80, reply[0]);
        Assert.Equal(0xD3, reply[1]);                                           // PT 83 | marker
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)));
        Assert.Equal(0xDEADBEEFCAFEF00DUL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)));
        Assert.Equal(0x1111111122222222UL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16)));
        Assert.Equal(0x3333333344444444UL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24)));
    }

    [Fact]
    public void Timing_reply_rejects_a_short_request_instead_of_reading_past_it()
    {
        var reply = new byte[32];
        int n = RtpPacket.WriteTimingReply(reply, new byte[8], received: 1, transmit: 2);
        _out.WriteLine($"short request -> {n} bytes written");
        Assert.Equal(0, n);
    }

    // ---- retransmit request -------------------------------------------------------------

    [Fact]
    public void Retransmit_requests_are_recognised_by_payload_type_85()
    {
        var request = new byte[8];
        request[0] = 0x80;
        request[1] = 0x55;                                    // PT 85, marker clear
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 4242);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), 3);

        Assert.True(RtpPacket.IsRetransmitRequest(request));
        Assert.True(RtpPacket.ParseRetransmitRequest(request, out ushort seq, out ushort count));
        _out.WriteLine($"retransmit request: seq={seq} count={count}");
        Assert.Equal(4242, seq);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Retransmit_detection_ignores_the_marker_bit_and_other_payload_types()
    {
        var marked = new byte[8] { 0x80, 0xD5, 0, 0, 0, 0, 0, 0 };   // PT 85 with marker set
        var sync = new byte[20] { 0x80, 0xD4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        _out.WriteLine($"marked PT85 -> {RtpPacket.IsRetransmitRequest(marked)}, "
                     + $"sync PT84 -> {RtpPacket.IsRetransmitRequest(sync)}");

        Assert.True(RtpPacket.IsRetransmitRequest(marked));
        Assert.False(RtpPacket.IsRetransmitRequest(sync));
        Assert.False(RtpPacket.IsRetransmitRequest(new byte[1]));
    }

    // ---- NTP ----------------------------------------------------------------------------

    [Fact]
    public void Ntp_uses_the_1900_epoch_in_the_high_word()
    {
        // 1900-01-01 is exactly 0. 1970-01-01 is 2208988800 seconds later — the constant every
        // NTP implementation carries.
        var epoch1900 = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var epoch1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _out.WriteLine($"1900 -> 0x{NtpTime.FromUtc(epoch1900):X16}");
        _out.WriteLine($"1970 -> 0x{NtpTime.FromUtc(epoch1970):X16}");

        Assert.Equal(0UL, NtpTime.FromUtc(epoch1900));
        Assert.Equal(2208988800UL << 32, NtpTime.FromUtc(epoch1970));
    }

    [Fact]
    public void Ntp_round_trips_to_within_a_millisecond()
    {
        var when = new DateTime(2026, 8, 26, 9, 12, 31, 456, DateTimeKind.Utc);
        ulong ntp = NtpTime.FromUtc(when);
        var back = NtpTime.ToUtc(ntp);
        _out.WriteLine($"{when:O} -> 0x{ntp:X16} -> {back:O} (delta {(back - when).TotalMilliseconds:F6} ms)");

        Assert.True(Math.Abs((back - when).TotalMilliseconds) < 1.0);
    }

    [Fact]
    public void Ntp_now_is_monotonic_enough_to_be_a_timestamp()
    {
        ulong a = NtpTime.Now();
        Thread.Sleep(20);
        ulong b = NtpTime.Now();
        _out.WriteLine($"a=0x{a:X16} b=0x{b:X16} delta={(b - a) / 4294967296.0 * 1000:F3} ms");
        Assert.True(b > a, "NTP Now() did not advance across a 20 ms sleep");
    }
}
