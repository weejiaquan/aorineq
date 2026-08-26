namespace AorinEQ.Core.Raop;

/// <summary>A ring of recently-sent audio packets, so the sender can answer the receiver's
/// retransmit requests (RTP payload type 85) instead of leaving a hole in the audio.
///
/// Measured against real hardware, roughly 4.5% of packets drew a resend request at a two
/// second queue — every one the sender cannot answer is an audible gap, and a shorter queue
/// produces more of them. This is the component that makes "realtime" mode usable rather than
/// merely fast.
///
/// Each slot records the sequence number it holds. RTP sequence is a ushort and wraps every
/// nine minutes or so, and slots are shared by every sequence congruent modulo capacity, so
/// without that check an evicted packet's slot would happily answer with its successor's bytes
/// — audio from thousands of packets away, which is worse than silence.
///
/// Thread-safe: the send thread stores while the control-channel thread reads.</summary>
public sealed class ResendBuffer
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly byte[]?[] _packets;
    private readonly int[] _sequences;   // -1 = empty; int so it can hold "no sequence"
    private int _count;
    private long _hits, _misses;

    public ResendBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive");
        _capacity = capacity;
        _packets = new byte[capacity][];
        _sequences = new int[capacity];
        Array.Fill(_sequences, -1);
    }

    public int Count { get { lock (_gate) return _count; } }
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Copies the packet in. The caller reuses one send buffer, so this must copy —
    /// storing the reference would leave every entry pointing at the newest packet.</summary>
    public void Store(ushort seq, ReadOnlySpan<byte> packet)
    {
        var copy = packet.ToArray();
        lock (_gate)
        {
            int slot = seq % _capacity;
            if (_sequences[slot] < 0) _count++;
            _sequences[slot] = seq;
            _packets[slot] = copy;
        }
    }

    /// <summary>Returns the packet for <paramref name="seq"/> if it is still held. A slot whose
    /// recorded sequence differs is a miss, never a wrong-packet hit.</summary>
    public bool TryGet(ushort seq, out byte[] packet)
    {
        lock (_gate)
        {
            int slot = seq % _capacity;
            if (_sequences[slot] == seq && _packets[slot] is { } stored)
            {
                packet = stored;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        packet = [];
        Interlocked.Increment(ref _misses);
        return false;
    }
}
