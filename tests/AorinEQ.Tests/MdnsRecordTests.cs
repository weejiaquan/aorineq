using System.Buffers.Binary;
using System.Text;
using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>DNS wire-format parsing for mDNS service discovery.
///
/// The fixtures are built here to RFC 1035 shape rather than captured, so the test states what
/// the format IS rather than what one device happened to send. Name compression is exercised
/// deliberately: real responders use it heavily (every record in a response repeats the service
/// name), and a parser that ignores 0xC0 pointers reads garbage rather than failing.</summary>
public class MdnsRecordTests
{
    private readonly ITestOutputHelper _out;
    public MdnsRecordTests(ITestOutputHelper output) => _out = output;

    // ---- fixture builder ----------------------------------------------------------------

    private static void WriteName(List<byte> to, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            to.Add((byte)label.Length);
            to.AddRange(Encoding.UTF8.GetBytes(label));
        }
        to.Add(0);
    }

    private static void WriteHeader(List<byte> to, int answers)
    {
        to.AddRange(new byte[] { 0, 0 });          // id
        to.AddRange(new byte[] { 0x84, 0 });       // flags: response, authoritative
        to.AddRange(new byte[] { 0, 0 });          // qdcount
        to.AddRange([(byte)(answers >> 8), (byte)answers]);
        to.AddRange(new byte[] { 0, 0, 0, 0 });    // ns, ar
    }

    private static void WriteRecord(List<byte> to, string name, ushort type, byte[] rdata)
    {
        WriteName(to, name);
        to.AddRange([(byte)(type >> 8), (byte)type]);
        to.AddRange(new byte[] { 0, 1 });                   // class IN
        to.AddRange(new byte[] { 0, 0, 0x11, 0x94 });       // ttl 4500
        to.AddRange([(byte)(rdata.Length >> 8), (byte)rdata.Length]);
        to.AddRange(rdata);
    }

    private static byte[] NameBytes(string name)
    {
        var b = new List<byte>();
        WriteName(b, name);
        return b.ToArray();
    }

    private static byte[] SrvRdata(int port, string target)
    {
        var b = new List<byte>();
        b.AddRange(new byte[] { 0, 0, 0, 0 });              // priority, weight
        b.AddRange([(byte)(port >> 8), (byte)port]);
        WriteName(b, target);
        return b.ToArray();
    }

    private static byte[] TxtRdata(params string[] entries)
    {
        var b = new List<byte>();
        foreach (var e in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(e);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        return b.ToArray();
    }

    // ---- query --------------------------------------------------------------------------

    [Fact]
    public void Query_asks_for_one_ptr_record()
    {
        var q = MdnsRecords.BuildQuery("_raop._tcp.local");
        _out.WriteLine($"query: {Convert.ToHexString(q)}");

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));   // qdcount
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(6)));   // ancount
        // labels then QTYPE=12 (PTR), QCLASS=1 (IN)
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 2)));
    }

    // ---- parsing ------------------------------------------------------------------------

    [Fact]
    public void Parses_ptr_srv_txt_and_a_records()
    {
        var msg = new List<byte>();
        WriteHeader(msg, 4);
        WriteRecord(msg, "_raop._tcp.local", 12, NameBytes("BE4DBCD7755B@Bedroom._raop._tcp.local"));
        WriteRecord(msg, "BE4DBCD7755B@Bedroom._raop._tcp.local", 33, SrvRdata(7000, "Bedroom.local"));
        WriteRecord(msg, "BE4DBCD7755B@Bedroom._raop._tcp.local", 16,
            TxtRdata("cn=0,1,2,3", "et=0,3,5", "tp=UDP", "am=AudioAccessory5,1"));
        WriteRecord(msg, "Bedroom.local", 1, new byte[] { 192, 168, 0, 100 });

        var parsed = MdnsRecords.ParseResponse(msg.ToArray());
        _out.WriteLine($"ptr={parsed.Ptr.Count} srv={parsed.Srv.Count} txt={parsed.Txt.Count} a={parsed.A.Count}");

        Assert.Equal("BE4DBCD7755B@Bedroom._raop._tcp.local", parsed.Ptr[0].Target);
        Assert.Equal(7000, parsed.Srv[0].Port);
        Assert.Equal("Bedroom.local", parsed.Srv[0].Target);
        Assert.Equal("192.168.0.100", parsed.A[0].Address);
        Assert.Equal("0,1,2,3", parsed.Txt[0].Values["cn"]);
        Assert.Equal("AudioAccessory5,1", parsed.Txt[0].Values["am"]);
    }

    [Fact]
    public void Resolves_a_compression_pointer_to_an_earlier_name()
    {
        // Record two's name is a 0xC0 pointer back to offset 12 - the first record's name.
        // Every real responder does this; a parser that does not follow it reads nonsense.
        var msg = new List<byte>();
        WriteHeader(msg, 2);
        WriteRecord(msg, "_raop._tcp.local", 12, NameBytes("Living._raop._tcp.local"));

        msg.AddRange(new byte[] { 0xC0, 12 });               // pointer to the header-adjacent name
        msg.AddRange(new byte[] { 0, 16 });                  // TXT
        msg.AddRange(new byte[] { 0, 1 });
        msg.AddRange(new byte[] { 0, 0, 0x11, 0x94 });
        var rdata = TxtRdata("et=0");
        msg.AddRange([(byte)(rdata.Length >> 8), (byte)rdata.Length]);
        msg.AddRange(rdata);

        var parsed = MdnsRecords.ParseResponse(msg.ToArray());
        _out.WriteLine($"compressed TXT record name -> '{parsed.Txt[0].Name}'");

        Assert.Equal("_raop._tcp.local", parsed.Txt[0].Name);
        Assert.Equal("0", parsed.Txt[0].Values["et"]);
    }

    [Fact]
    public void Txt_handles_a_valueless_key_and_an_equals_inside_a_value()
    {
        var msg = new List<byte>();
        WriteHeader(msg, 1);
        WriteRecord(msg, "x._raop._tcp.local", 16, TxtRdata("da=true", "flag", "pk=aa=bb"));

        var parsed = MdnsRecords.ParseResponse(msg.ToArray());
        var txt = parsed.Txt[0].Values;
        _out.WriteLine(string.Join(", ", txt.Select(kv => $"{kv.Key}='{kv.Value}'")));

        Assert.Equal("true", txt["da"]);
        Assert.Equal("", txt["flag"]);        // present but valueless
        Assert.Equal("aa=bb", txt["pk"]);     // only the FIRST '=' separates
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(20)]
    public void Truncated_packets_return_empty_rather_than_throwing(int length)
    {
        var msg = new List<byte>();
        WriteHeader(msg, 1);
        WriteRecord(msg, "_raop._tcp.local", 12, NameBytes("a._raop._tcp.local"));
        var truncated = msg.Take(length).ToArray();

        var parsed = MdnsRecords.ParseResponse(truncated);
        _out.WriteLine($"{length} bytes -> ptr={parsed.Ptr.Count} srv={parsed.Srv.Count}");
        Assert.Empty(parsed.Ptr);
    }

    [Fact]
    public void A_compression_pointer_loop_terminates()
    {
        // Malformed input must not hang the discovery thread.
        var msg = new List<byte>();
        WriteHeader(msg, 1);
        msg.AddRange(new byte[] { 0xC0, 12 });   // offset 12 points at itself
        msg.AddRange(new byte[] { 0, 12, 0, 1, 0, 0, 0x11, 0x94, 0, 0 });

        var parsed = MdnsRecords.ParseResponse(msg.ToArray());
        _out.WriteLine($"self-referential pointer survived: ptr={parsed.Ptr.Count}");
        Assert.True(true, "parsing returned rather than looping");
    }

    [Fact]
    public void Device_display_name_strips_the_hardware_prefix()
    {
        var device = new AirPlayDevice("BE4DBCD7755B@Bedroom", "Bedroom.local", "192.168.0.100",
            7000, new Dictionary<string, string>());
        _out.WriteLine($"'{device.Name}' -> '{device.DisplayName}'");
        Assert.Equal("Bedroom", device.DisplayName);
    }

    [Fact]
    public void Device_display_name_survives_a_name_with_no_prefix()
    {
        var device = new AirPlayDevice("Kitchen", "Kitchen.local", "10.0.0.5", 7000,
            new Dictionary<string, string>());
        _out.WriteLine($"'{device.Name}' -> '{device.DisplayName}'");
        Assert.Equal("Kitchen", device.DisplayName);
    }
}
