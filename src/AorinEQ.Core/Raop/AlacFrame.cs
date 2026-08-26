namespace AorinEQ.Core.Raop;

/// <summary>ALAC framing via the format's UNCOMPRESSED escape path.
///
/// This is why the sender needs no codec library and no native dependency: ALAC can carry raw
/// samples behind a short header, and RAOP receivers accept that as readily as a compressed
/// frame. The cost is bandwidth (~1392 kbps) which is irrelevant on a LAN.
///
/// Layout, confirmed against Apple's ALACDecoder.cpp and FFmpeg's alac.c:
///   3  bits  element tag        1 = CPE, a stereo channel pair
///   4  bits  element instance   0
///   12 bits  unused header      0
///   1  bit   has_size           0 = the frame is the default length
///   2  bits  extra / wasted     0
///   1  bit   escape             1 = NOT compressed; raw samples follow
/// then interleaved big-endian 16-bit L,R samples, padded to a byte boundary.
///
/// 23 header bits means the payload is not byte-aligned — see <see cref="BitWriter"/>.</summary>
public static class AlacFrame
{
    /// <summary>Frames per RAOP packet. Fixed by the protocol and echoed in the SDP fmtp line;
    /// 352 frames at 44100 Hz is 7.98 ms of audio.</summary>
    public const int FramesPerPacket = 352;

    private const int HeaderBits = 23;
    private const int BitsPerFrame = 32; // two channels * 16 bits

    /// <summary>Bytes needed for <paramref name="frames"/> stereo frames, header included.</summary>
    public static int MaxPayloadBytes(int frames) => (HeaderBits + frames * BitsPerFrame + 7) / 8;

    /// <summary>Packs one stereo block. <paramref name="interleaved"/> holds
    /// <paramref name="frames"/> * 2 samples as L,R,L,R. Returns the payload length.</summary>
    public static int PackUncompressed(ReadOnlySpan<short> interleaved, int frames, Span<byte> dest)
    {
        int needed = MaxPayloadBytes(frames);
        if (dest.Length < needed)
            throw new ArgumentException(
                $"need {needed} bytes for {frames} frames, got {dest.Length}", nameof(dest));
        if (interleaved.Length < frames * 2)
            throw new ArgumentException(
                $"need {frames * 2} samples for {frames} stereo frames, got {interleaved.Length}",
                nameof(interleaved));

        // The caller reuses this buffer packet after packet, and BitWriter ORs bits in. Without
        // this clear, silence written over music would still carry the music's bits.
        dest[..needed].Clear();

        var w = new BitWriter(dest);
        w.Write(1, 3);    // element tag: CPE (stereo)
        w.Write(0, 4);    // element instance tag
        w.Write(0, 12);   // unused header bits
        w.Write(0, 1);    // has_size = 0
        w.Write(0, 2);    // extra / wasted bits
        w.Write(1, 1);    // escape = 1 -> uncompressed

        for (int i = 0; i < frames * 2; i++)
            w.Write((ushort)interleaved[i], 16);

        return w.ByteLength;
    }
}
