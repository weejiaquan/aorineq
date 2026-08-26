using AorinEQ.Core.Raop;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>RTSP response parsing and the ANNOUNCE SDP body.
///
/// Both are pure functions deliberately: the socket half of RtspClient needs a receiver on the
/// network, but the parsing and the SDP text — where the bugs actually live — do not.</summary>
public class RtspTests
{
    private readonly ITestOutputHelper _out;
    public RtspTests(ITestOutputHelper output) => _out = output;

    private const string OptionsOk =
        "RTSP/1.0 200 OK\r\n" +
        "Date: Wed, 26 Aug 2026 08:47:35 GMT\r\n" +
        "Content-Length: 0\r\n" +
        "Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS\r\n" +
        "Server: AirTunes/960.13.1\r\n" +
        "CSeq: 1\r\n\r\n";

    [Fact]
    public void Parses_status_reason_and_headers()
    {
        var r = RtspClient.ParseResponse(OptionsOk, "");
        _out.WriteLine($"{r.Status} {r.Reason}; {r.Headers.Count} headers");

        Assert.Equal(200, r.Status);
        Assert.Equal("OK", r.Reason);
        Assert.Equal("AirTunes/960.13.1", r.Header("Server"));
    }

    [Fact]
    public void Header_lookup_is_case_insensitive()
    {
        var r = RtspClient.ParseResponse(OptionsOk, "");
        _out.WriteLine($"Server/server/SERVER -> {r.Header("Server")}/{r.Header("server")}/{r.Header("SERVER")}");

        Assert.Equal(r.Header("Server"), r.Header("server"));
        Assert.Equal(r.Header("Server"), r.Header("SERVER"));
        Assert.Null(r.Header("No-Such-Header"));
    }

    [Fact]
    public void Parses_the_setup_transport_response_seen_on_the_wire()
    {
        const string setup =
            "RTSP/1.0 200 OK\r\n" +
            "Transport: RTP/AVP/UDP;unicast;mode=record;server_port=63877;control_port=50635;timing_port=0\r\n" +
            "Session: 1\r\n" +
            "Audio-Jack-Status: connected\r\n\r\n";
        var r = RtspClient.ParseResponse(setup, "");
        var transport = RtspClient.ParseTransport(r.Header("Transport") ?? "");
        _out.WriteLine($"server_port={transport["server_port"]} control_port={transport["control_port"]} "
                     + $"timing_port={transport["timing_port"]}");

        Assert.Equal("63877", transport["server_port"]);
        Assert.Equal("50635", transport["control_port"]);
        // The receiver reports timing_port=0 and yet still sends timing requests. Recorded here
        // so nobody "fixes" the session by trusting this field.
        Assert.Equal("0", transport["timing_port"]);
        Assert.Equal("1", r.Header("Session"));
    }

    [Fact]
    public void Session_header_drops_any_timeout_parameter()
    {
        var r = RtspClient.ParseResponse("RTSP/1.0 200 OK\r\nSession: DEADBEEF;timeout=60\r\n\r\n", "");
        _out.WriteLine($"session id -> {RtspClient.ParseSessionId(r.Header("Session"))}");
        Assert.Equal("DEADBEEF", RtspClient.ParseSessionId(r.Header("Session")));
    }

    [Fact]
    public void Tolerates_a_status_line_with_no_reason_phrase()
    {
        var r = RtspClient.ParseResponse("RTSP/1.0 456\r\n\r\n", "");
        _out.WriteLine($"status={r.Status} reason='{r.Reason}'");
        Assert.Equal(456, r.Status);
        Assert.Equal("", r.Reason);
    }

    [Fact]
    public void Carries_a_body_through()
    {
        var r = RtspClient.ParseResponse("RTSP/1.0 200 OK\r\nContent-Length: 5\r\n\r\n", "hello");
        _out.WriteLine($"body='{r.Body}'");
        Assert.Equal("hello", r.Body);
    }

    // ---- SDP ----------------------------------------------------------------------------

    [Fact]
    public void Sdp_connection_address_is_the_sender_not_the_receiver()
    {
        // THE regression guard for this feature. Putting the receiver's address in c= makes
        // ANNOUNCE return 200 and then SETUP hang forever with no error - the single most
        // expensive bug found while building this. c= must equal the o= address.
        string sdp = RaopSdp.Build(sessionId: 3121287335, localAddress: "192.168.0.104",
            framesPerPacket: 352, sampleRate: 44100);
        _out.WriteLine(sdp);

        Assert.Contains("o=iTunes 3121287335 0 IN IP4 192.168.0.104\r\n", sdp);
        Assert.Contains("c=IN IP4 192.168.0.104\r\n", sdp);
        Assert.DoesNotContain("192.168.0.100", sdp);
    }

    [Fact]
    public void Sdp_declares_unencrypted_alac()
    {
        string sdp = RaopSdp.Build(1, "10.0.0.1", 352, 44100);
        _out.WriteLine(sdp);

        Assert.Contains("m=audio 0 RTP/AVP 96\r\n", sdp);
        Assert.Contains("a=rtpmap:96 AppleLossless\r\n", sdp);
        Assert.Contains("a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n", sdp);
        // No key material: this receiver accepts et=0, and adding these would demand crypto
        // the sender deliberately does not carry.
        Assert.DoesNotContain("rsaaeskey", sdp);
        Assert.DoesNotContain("aesiv", sdp);
    }

    [Fact]
    public void Sdp_uses_crlf_throughout()
    {
        string sdp = RaopSdp.Build(1, "10.0.0.1", 352, 44100);
        int bareNewlines = 0;
        for (int i = 0; i < sdp.Length; i++)
            if (sdp[i] == '\n' && (i == 0 || sdp[i - 1] != '\r')) bareNewlines++;
        _out.WriteLine($"lines={sdp.Split("\r\n").Length - 1} bare LFs={bareNewlines}");

        Assert.Equal(0, bareNewlines);
        Assert.StartsWith("v=0\r\n", sdp);
    }
}
