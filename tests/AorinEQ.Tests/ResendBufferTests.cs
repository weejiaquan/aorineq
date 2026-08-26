using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The ring of recently-sent packets that answers the receiver's retransmit requests.
///
/// Measured on real hardware: about 4.5% of packets drew a resend request at a 2 s queue. Every
/// one of those the sender cannot answer is an audible gap, so the correctness that matters
/// here is "never return the wrong packet" — a stale slot mistaken for a hit sends the receiver
/// audio from 65536 packets ago, which is worse than sending nothing.</summary>
public class ResendBufferTests
{
    private readonly ITestOutputHelper _out;
    public ResendBufferTests(ITestOutputHelper output) => _out = output;

    private static byte[] Packet(int seed, int length = 16)
    {
        var p = new byte[length];
        for (int i = 0; i < length; i++) p[i] = (byte)(seed + i);
        return p;
    }

    [Fact]
    public void Stores_and_returns_a_packet()
    {
        var buffer = new ResendBuffer(8);
        buffer.Store(42, Packet(1));

        Assert.True(buffer.TryGet(42, out var got));
        _out.WriteLine($"seq 42 -> {Convert.ToHexString(got)} (hits={buffer.Hits})");
        Assert.Equal(Packet(1), got);
        Assert.Equal(1, buffer.Hits);
    }

    [Fact]
    public void An_unknown_sequence_is_a_miss()
    {
        var buffer = new ResendBuffer(8);
        buffer.Store(42, Packet(1));

        Assert.False(buffer.TryGet(43, out _));
        _out.WriteLine($"misses={buffer.Misses}");
        Assert.Equal(1, buffer.Misses);
    }

    [Fact]
    public void The_oldest_packet_is_evicted_once_capacity_is_exceeded()
    {
        var buffer = new ResendBuffer(4);
        for (ushort seq = 100; seq < 108; seq++)
            buffer.Store(seq, Packet(seq));

        _out.WriteLine($"after 8 stores into capacity 4: count={buffer.Count}");
        Assert.False(buffer.TryGet(100, out _));   // evicted
        Assert.False(buffer.TryGet(103, out _));   // evicted
        Assert.True(buffer.TryGet(104, out _));    // still present
        Assert.True(buffer.TryGet(107, out _));
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Sequence_numbers_wrapping_past_65535_still_resolve()
    {
        // RTP sequence is a ushort. The stream wraps every ~9 minutes at 352 frames/packet,
        // and a slot holding a pre-wrap packet must not answer a post-wrap request.
        var buffer = new ResendBuffer(8);
        ushort[] around = [65533, 65534, 65535, 0, 1, 2];
        foreach (var seq in around)
            buffer.Store(seq, Packet(seq == 0 ? 200 : seq));

        foreach (var seq in around)
        {
            bool hit = buffer.TryGet(seq, out var got);
            _out.WriteLine($"seq {seq,5} -> {(hit ? Convert.ToHexString(got.AsSpan(0, 4)) : "MISS")}");
            Assert.True(hit, $"sequence {seq} was lost across the wrap");
        }
    }

    [Fact]
    public void A_wrapped_slot_never_answers_with_the_previous_occupants_packet()
    {
        // seq 3 and seq 3+capacity share a slot. Asking for the evicted one must miss, not
        // return its successor's bytes.
        var buffer = new ResendBuffer(4);
        buffer.Store(3, Packet(30));
        buffer.Store(7, Packet(70));   // same slot as 3

        bool staleHit = buffer.TryGet(3, out var got);
        _out.WriteLine($"evicted seq 3 -> {(staleHit ? Convert.ToHexString(got) : "MISS (correct)")}");
        Assert.False(staleHit);
        Assert.True(buffer.TryGet(7, out var fresh));
        Assert.Equal(Packet(70), fresh);
    }

    [Fact]
    public void Stored_packets_are_copies_not_references()
    {
        // The sender reuses one packet buffer, so the ring must copy or every entry would end
        // up holding the most recent packet's bytes.
        var buffer = new ResendBuffer(4);
        var scratch = Packet(1);
        buffer.Store(1, scratch);
        Array.Fill(scratch, (byte)0xFF);

        Assert.True(buffer.TryGet(1, out var got));
        _out.WriteLine($"stored={Convert.ToHexString(got.AsSpan(0, 4))} scratch now={Convert.ToHexString(scratch.AsSpan(0, 4))}");
        Assert.Equal(Packet(1), got);
    }

    [Fact]
    public void Variable_length_packets_round_trip_exactly()
    {
        var buffer = new ResendBuffer(4);
        buffer.Store(1, Packet(1, 1423));
        buffer.Store(2, Packet(2, 64));

        Assert.True(buffer.TryGet(1, out var big));
        Assert.True(buffer.TryGet(2, out var small));
        _out.WriteLine($"lengths: {big.Length}, {small.Length}");
        Assert.Equal(1423, big.Length);
        Assert.Equal(64, small.Length);
    }
}
