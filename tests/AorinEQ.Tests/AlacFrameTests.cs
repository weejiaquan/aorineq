using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>ALAC's uncompressed escape path — the reason this project needs no codec library.
///
/// The frame header is 23 bits, so the audio that follows is NOT byte-aligned. That single
/// fact is why a bit writer exists at all, and why these tests read samples back out by
/// counting bits rather than by indexing bytes: an off-by-one in the header shifts every
/// sample in the frame and produces noise, not an error.
///
/// Layout confirmed against Apple's own ALACDecoder.cpp and FFmpeg's alac.c:
///   3 element tag (1 = CPE, stereo) | 4 instance | 12 unused | 1 has_size | 2 extra | 1 escape
/// </summary>
public class AlacFrameTests
{
    private readonly ITestOutputHelper _out;
    public AlacFrameTests(ITestOutputHelper output) => _out = output;

    /// <summary>Reads <paramref name="bits"/> bits MSB-first starting at an absolute bit
    /// offset — the only honest way to verify a stream whose payload starts at bit 23.</summary>
    private static uint ReadBits(byte[] buffer, int bitOffset, int bits)
    {
        uint value = 0;
        for (int i = 0; i < bits; i++)
        {
            int pos = bitOffset + i;
            int bit = (buffer[pos >> 3] >> (7 - (pos & 7))) & 1;
            value = (value << 1) | (uint)bit;
        }
        return value;
    }

    [Fact]
    public void BitWriter_packs_msb_first_across_a_byte_boundary()
    {
        var buf = new byte[3];
        var w = new BitWriter(buf);
        w.Write(0b101, 3);       // bits 0..2
        w.Write(0b1111_0000, 8); // bits 3..10, straddles the byte boundary
        _out.WriteLine($"packed: {Convert.ToHexString(buf)} ({w.ByteLength} bytes)");

        // 101 11110000 -> 10111110 000_____
        Assert.Equal(0b1011_1110, buf[0]);
        Assert.Equal(0b0000_0000, buf[1]);
        Assert.Equal(2, w.ByteLength);
    }

    [Fact]
    public void BitWriter_reports_length_rounded_up_to_whole_bytes()
    {
        var w = new BitWriter(new byte[4]);
        w.Write(0, 23);
        _out.WriteLine($"23 bits -> {w.ByteLength} bytes");
        Assert.Equal(3, w.ByteLength);
    }

    [Fact]
    public void Header_is_twenty_three_bits_ending_in_the_escape_flag()
    {
        // All-zero samples make every payload bit zero, so the first three bytes are the
        // header alone: 001 0000 000000000000 0 00 1 then one zero sample bit.
        var pcm = new short[8 * 2];
        var dest = new byte[AlacFrame.MaxPayloadBytes(8)];
        AlacFrame.PackUncompressed(pcm, 8, dest);
        _out.WriteLine($"header bytes: {Convert.ToHexString(dest.AsSpan(0, 3))}");

        Assert.Equal(0x20, dest[0]);   // 00100000 -> tag=001 (CPE), instance starts
        Assert.Equal(0x00, dest[1]);
        Assert.Equal(0x02, dest[2]);   // 00000010 -> escape bit set at bit 22

        Assert.Equal(1u, ReadBits(dest, 0, 3));    // element tag = CPE
        Assert.Equal(0u, ReadBits(dest, 3, 4));    // instance tag
        Assert.Equal(0u, ReadBits(dest, 7, 12));   // unused header
        Assert.Equal(0u, ReadBits(dest, 19, 1));   // has_size
        Assert.Equal(0u, ReadBits(dest, 20, 2));   // extra bits
        Assert.Equal(1u, ReadBits(dest, 22, 1));   // escape = uncompressed
    }

    [Fact]
    public void Samples_follow_the_header_interleaved_left_then_right()
    {
        var pcm = new short[] { 0x1234, 0x5678, -2, 0x0001 };  // two frames, L R L R
        var dest = new byte[AlacFrame.MaxPayloadBytes(2)];
        AlacFrame.PackUncompressed(pcm, 2, dest);
        _out.WriteLine($"packed: {Convert.ToHexString(dest)}");

        Assert.Equal(0x1234u, ReadBits(dest, 23, 16));
        Assert.Equal(0x5678u, ReadBits(dest, 39, 16));
        Assert.Equal(0xFFFEu, ReadBits(dest, 55, 16));   // -2 as two's complement
        Assert.Equal(0x0001u, ReadBits(dest, 71, 16));
    }

    [Fact]
    public void A_full_packet_is_1411_bytes()
    {
        // 23 header bits + 352 frames * 2 channels * 16 bits = 11287 bits = 1411 bytes.
        // This is the payload size observed on the wire from a working sender.
        var pcm = new short[AlacFrame.FramesPerPacket * 2];
        var dest = new byte[AlacFrame.MaxPayloadBytes(AlacFrame.FramesPerPacket)];
        int n = AlacFrame.PackUncompressed(pcm, AlacFrame.FramesPerPacket, dest);
        _out.WriteLine($"{AlacFrame.FramesPerPacket} frames -> {n} bytes payload");

        Assert.Equal(1411, n);
        Assert.Equal(352, AlacFrame.FramesPerPacket);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(352)]
    public void Length_is_always_the_bit_count_rounded_up(int frames)
    {
        var pcm = new short[frames * 2];
        var dest = new byte[AlacFrame.MaxPayloadBytes(frames)];
        int n = AlacFrame.PackUncompressed(pcm, frames, dest);
        int expected = (23 + frames * 32 + 7) / 8;
        _out.WriteLine($"{frames} frames -> {n} bytes (expected {expected})");
        Assert.Equal(expected, n);
    }

    [Fact]
    public void A_destination_too_small_is_rejected_rather_than_overrunning()
    {
        var pcm = new short[AlacFrame.FramesPerPacket * 2];
        var tooSmall = new byte[16];
        var ex = Record.Exception(() =>
            AlacFrame.PackUncompressed(pcm, AlacFrame.FramesPerPacket, tooSmall));
        _out.WriteLine($"undersized destination -> {ex?.GetType().Name ?? "no exception"}");
        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public void Packing_does_not_carry_bits_over_from_a_reused_buffer()
    {
        // The sender reuses one payload buffer for every packet. A frame of silence written
        // over a frame of music must not inherit stale bits.
        var dest = new byte[AlacFrame.MaxPayloadBytes(AlacFrame.FramesPerPacket)];
        var loud = new short[AlacFrame.FramesPerPacket * 2];
        Array.Fill(loud, unchecked((short)0xFFFF));
        AlacFrame.PackUncompressed(loud, AlacFrame.FramesPerPacket, dest);

        var silence = new short[AlacFrame.FramesPerPacket * 2];
        int n = AlacFrame.PackUncompressed(silence, AlacFrame.FramesPerPacket, dest);
        int nonZeroAfterHeader = 0;
        for (int i = 3; i < n; i++) if (dest[i] != 0) nonZeroAfterHeader++;
        _out.WriteLine($"non-zero payload bytes after re-pack: {nonZeroAfterHeader}");

        Assert.Equal(0, nonZeroAfterHeader);
    }
}
