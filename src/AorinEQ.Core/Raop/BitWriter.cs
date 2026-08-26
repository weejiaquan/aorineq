namespace AorinEQ.Core.Raop;

/// <summary>MSB-first bit writer over a caller-owned span.
///
/// It exists for exactly one reason: an ALAC uncompressed frame header is 23 bits, so the
/// samples that follow are not byte-aligned and cannot be written with byte indexing.
///
/// A ref struct so it can write straight into the sender's packet buffer — the alternative is
/// packing into a scratch array and copying, once every 8 ms, forever.
///
/// Bits are OR-ed in, so the target must be zeroed first; <see cref="AlacFrame"/> does that
/// because the sender reuses one buffer per packet and stale bits would leak the previous
/// packet's audio into this one.</summary>
public ref struct BitWriter
{
    private readonly Span<byte> _buffer;
    private int _bitPos;

    public BitWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _bitPos = 0;
    }

    /// <summary>Total bytes touched so far, rounded up — the payload length.</summary>
    public readonly int ByteLength => (_bitPos + 7) >> 3;

    public void Write(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
                _buffer[_bitPos >> 3] |= (byte)(1 << (7 - (_bitPos & 7)));
            _bitPos++;
        }
    }
}
