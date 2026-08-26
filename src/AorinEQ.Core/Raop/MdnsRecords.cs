using System.Buffers.Binary;
using System.Text;

namespace AorinEQ.Core.Raop;

/// <summary>One AirPlay receiver found on the network.</summary>
public sealed record AirPlayDevice(
    string Name, string HostName, string Address, int Port,
    IReadOnlyDictionary<string, string> Txt)
{
    /// <summary>The part a person recognises. RAOP instance names are
    /// <c>&lt;mac&gt;@&lt;room&gt;</c>; anything without the prefix is used as-is.</summary>
    public string DisplayName
    {
        get
        {
            int at = Name.IndexOf('@');
            return at >= 0 && at < Name.Length - 1 ? Name[(at + 1)..] : Name;
        }
    }

    /// <summary>Stable identity across restarts — the instance name, which carries the MAC.</summary>
    public string Id => Name;
}

public sealed record PtrRecord(string Name, string Target);
public sealed record SrvRecord(string Name, string Target, int Port);
public sealed record TxtRecord(string Name, IReadOnlyDictionary<string, string> Values);
public sealed record ARecord(string Name, string Address);

public sealed class MdnsMessage
{
    public List<PtrRecord> Ptr { get; } = [];
    public List<SrvRecord> Srv { get; } = [];
    public List<TxtRecord> Txt { get; } = [];
    public List<ARecord> A { get; } = [];
}

/// <summary>DNS wire format, enough of it for service discovery.
///
/// Hand-rolled rather than taking a NuGet dependency: AorinEQ.Core carries zero packages and
/// the browser only needs to ask one question and read four record types.
///
/// Everything here is pure and total — a malformed or truncated packet yields fewer records,
/// never an exception. Discovery runs on a background thread against whatever the network
/// sends, which includes responders that are not the one being looked for.</summary>
public static class MdnsRecords
{
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const int HeaderBytes = 12;

    /// <summary>A standard query for the PTR records of one service type.</summary>
    public static byte[] BuildQuery(string serviceName)
    {
        var packet = new List<byte>(64);
        packet.AddRange(new byte[] { 0, 0 });   // transaction id: 0 for mDNS
        packet.AddRange(new byte[] { 0, 0 });   // flags: standard query
        packet.AddRange(new byte[] { 0, 1 });   // qdcount
        packet.AddRange(new byte[] { 0, 0 });   // ancount
        packet.AddRange(new byte[] { 0, 0 });   // nscount
        packet.AddRange(new byte[] { 0, 0 });   // arcount
        foreach (var label in serviceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.UTF8.GetBytes(label));
        }
        packet.Add(0);
        packet.AddRange(new byte[] { 0, (byte)TypePtr });
        packet.AddRange(new byte[] { 0, 1 });   // class IN
        return packet.ToArray();
    }

    /// <summary>Reads the answer records out of a response. Questions are skipped; additional
    /// and authority sections are parsed the same way as answers, because responders scatter
    /// SRV/TXT/A across all three.</summary>
    public static MdnsMessage ParseResponse(ReadOnlySpan<byte> packet)
    {
        var message = new MdnsMessage();
        if (packet.Length < HeaderBytes)
            return message;

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
                    + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
                    + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);

        int offset = HeaderBytes;
        for (int i = 0; i < questions; i++)
        {
            if (!SkipName(packet, ref offset) || offset + 4 > packet.Length)
                return message;
            offset += 4;
        }

        for (int i = 0; i < answers; i++)
        {
            if (!TryReadName(packet, ref offset, out string name)) return message;
            if (offset + 10 > packet.Length) return message;

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (rdLength < 0 || offset + rdLength > packet.Length) return message;

            int rdStart = offset;
            switch (type)
            {
                case TypePtr:
                {
                    int p = rdStart;
                    if (TryReadName(packet, ref p, out string target))
                        message.Ptr.Add(new PtrRecord(name, target));
                    break;
                }
                case TypeSrv:
                {
                    if (rdLength >= 7)
                    {
                        int port = BinaryPrimitives.ReadUInt16BigEndian(packet[(rdStart + 4)..]);
                        int p = rdStart + 6;
                        if (TryReadName(packet, ref p, out string target))
                            message.Srv.Add(new SrvRecord(name, target, port));
                    }
                    break;
                }
                case TypeTxt:
                    message.Txt.Add(new TxtRecord(name,
                        ParseTxt(packet.Slice(rdStart, rdLength))));
                    break;
                case TypeA:
                    if (rdLength == 4)
                        message.A.Add(new ARecord(name,
                            $"{packet[rdStart]}.{packet[rdStart + 1]}.{packet[rdStart + 2]}.{packet[rdStart + 3]}"));
                    break;
            }
            offset = rdStart + rdLength;
        }
        return message;
    }

    /// <summary>TXT rdata is a run of length-prefixed strings, each usually
    /// <c>key=value</c>. Only the FIRST '=' separates — values legitimately contain more
    /// (base64 public keys do). A string with no '=' is a valueless flag.</summary>
    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> rdata)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < rdata.Length)
        {
            int len = rdata[i++];
            if (len == 0 || i + len > rdata.Length) break;
            string entry = Encoding.UTF8.GetString(rdata.Slice(i, len));
            i += len;
            int eq = entry.IndexOf('=');
            if (eq < 0) values[entry] = "";
            else values[entry[..eq]] = entry[(eq + 1)..];
        }
        return values;
    }

    private static bool SkipName(ReadOnlySpan<byte> packet, ref int offset) =>
        TryReadName(packet, ref offset, out _);

    /// <summary>Reads a possibly-compressed name, advancing <paramref name="offset"/> past it.
    ///
    /// A label length byte with its top two bits set is a 14-bit pointer to an earlier offset;
    /// responders use these constantly. The hop limit is a loop guard — a malformed packet can
    /// point a name at itself, and this runs on a background thread against arbitrary network
    /// input.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        var sb = new StringBuilder();
        int cursor = offset;
        bool jumped = false;
        int hops = 0;
        name = "";

        while (true)
        {
            if (cursor < 0 || cursor >= packet.Length) return false;
            byte len = packet[cursor];

            if (len == 0)
            {
                cursor++;
                if (!jumped) offset = cursor;
                name = sb.ToString();
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length) return false;
                int pointer = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped)
                {
                    offset = cursor + 2;
                    jumped = true;
                }
                if (++hops > 64) return false;   // self-referential or cyclic packet
                cursor = pointer;
                continue;
            }

            cursor++;
            if (cursor + len > packet.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(packet.Slice(cursor, len)));
            cursor += len;
        }
    }
}
