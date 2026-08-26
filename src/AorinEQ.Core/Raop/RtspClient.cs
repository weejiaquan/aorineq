using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AorinEQ.Core.Raop;

/// <summary>One parsed RTSP response. Header lookup is case-insensitive.</summary>
public sealed record RtspResponse(
    int Status, string Reason, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

    public bool Ok => Status == 200;
}

/// <summary>A minimal RTSP/1.0 client over TCP.
///
/// RTSP is HTTP-shaped text, so this is a request writer plus a header parser with no framing
/// beyond Content-Length. The parsing half is static and pure so it can be tested without a
/// receiver on the network; only the socket half needs hardware.
///
/// Every exchange is retained in <see cref="Exchanges"/> — the Settings page shows the real
/// conversation, which is the diagnostic no comparable product offers.</summary>
public sealed class RtspClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly List<string> _exchanges = new();
    private int _cseq;

    /// <summary>Sent on every request. Left as a plain product identifier: the receiver was
    /// verified to serve legacy RAOP regardless of this string.</summary>
    public const string UserAgent = "AorinEQ/1.0 (Windows)";

    public string ClientInstance { get; } = RandomHex(16);
    public string DacpId { get; } = RandomHex(16);
    public int ActiveRemote { get; } = Random.Shared.Next(1, int.MaxValue);
    public string? SessionId { get; set; }

    /// <summary>The full request/response text of every exchange, for diagnostics.</summary>
    public IReadOnlyList<string> Exchanges
    {
        get { lock (_exchanges) return _exchanges.ToList(); }
    }

    public RtspClient(string host, int port, int timeoutMs = 10_000)
    {
        // Explicit InterNetwork. A default TcpClient is dual-stack and its LocalEndPoint then
        // reports ::ffff:a.b.c.d, which lands verbatim in the RTSP URI and the SDP o=/c= lines
        // and leaves the receiver with no usable address.
        _tcp = new TcpClient(AddressFamily.InterNetwork)
        {
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs,
        };
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    /// <summary>This machine's address on the interface that actually reached the receiver —
    /// the value that must appear in the SDP.</summary>
    public string LocalAddress
    {
        get
        {
            var address = ((IPEndPoint)_tcp.Client.LocalEndPoint!).Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return address.ToString();
        }
    }

    private static string RandomHex(int chars)
    {
        var bytes = new byte[chars / 2];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>Sends one request and reads its response.
    ///
    /// Header ORDER — CSeq, Session, method headers, content headers, then the identity block —
    /// mirrors a sender known to work against this receiver. Order should not matter to a
    /// conformant parser; it is preserved so that "my request differs from the working one"
    /// can never be the explanation for a failure.</summary>
    public RtspResponse Send(string method, string uri,
        IReadOnlyDictionary<string, string>? headers = null,
        string? contentType = null, byte[]? body = null)
    {
        _cseq++;
        var sb = new StringBuilder();
        sb.Append($"{method} {uri} RTSP/1.0\r\n");
        sb.Append($"CSeq: {_cseq}\r\n");
        if (SessionId is not null)
            sb.Append($"Session: {SessionId}\r\n");
        if (headers is not null)
            foreach (var (k, v) in headers)
                sb.Append($"{k}: {v}\r\n");
        if (body is not null)
        {
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
        }
        sb.Append($"User-Agent: {UserAgent}\r\n");
        sb.Append($"Client-Instance: {ClientInstance}\r\n");
        sb.Append($"DACP-ID: {DacpId}\r\n");
        sb.Append($"Active-Remote: {ActiveRemote}\r\n");
        sb.Append("\r\n");

        string requestText = sb.ToString();
        var head = Encoding.ASCII.GetBytes(requestText);
        _stream.Write(head, 0, head.Length);
        if (body is not null)
            _stream.Write(body, 0, body.Length);
        _stream.Flush();

        var response = ReadResponse(out string responseText);
        lock (_exchanges)
        {
            string bodyText = body is not null && contentType == "application/sdp"
                ? Encoding.ASCII.GetString(body) : "";
            _exchanges.Add($">> {requestText}{bodyText}\n<< {responseText}");
        }
        return response;
    }

    private RtspResponse ReadResponse(out string raw)
    {
        string headText = ReadHeaderBlock();
        var headers = ParseHeaders(headText);

        string bodyText = "";
        if (headers.TryGetValue("content-length", out var lenText)
            && int.TryParse(lenText, out int len) && len > 0)
        {
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = _stream.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            bodyText = Encoding.ASCII.GetString(buf, 0, read);
        }
        raw = headText + bodyText;
        return ParseResponse(headText, bodyText);
    }

    /// <summary>Reads until the CRLFCRLF ending the header block. A byte at a time: the block
    /// is a few hundred bytes and buffering ahead would steal the body.</summary>
    private string ReadHeaderBlock()
    {
        var buf = new List<byte>(512);
        int match = 0;
        while (match < 4)
        {
            int b = _stream.ReadByte();
            if (b < 0)
                throw new IOException("RTSP connection closed while reading a response header");
            buf.Add((byte)b);
            match = b switch
            {
                '\r' when match is 0 or 2 => match + 1,
                '\n' when match is 1 or 3 => match + 1,
                _ => 0,
            };
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    private static Dictionary<string, string> ParseHeaders(string headText)
    {
        var headers = new Dictionary<string, string>();
        var lines = headText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    /// <summary>Pure: turns a header block and body into a response. Static so the parsing —
    /// where the bugs are — is testable without a socket.</summary>
    public static RtspResponse ParseResponse(string headText, string body)
    {
        var lines = headText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new RtspResponse(0, "", new Dictionary<string, string>(), body);

        var statusLine = lines[0].Split(' ', 3);
        int status = statusLine.Length > 1 && int.TryParse(statusLine[1], out int parsed) ? parsed : 0;
        string reason = statusLine.Length > 2 ? statusLine[2] : "";
        return new RtspResponse(status, reason, ParseHeaders(headText), body);
    }

    /// <summary>Splits a Transport header's semicolon-separated key=value parameters.</summary>
    public static Dictionary<string, string> ParseTransport(string header)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>The session id alone, without any ;timeout= parameter.</summary>
    public static string? ParseSessionId(string? sessionHeader) =>
        sessionHeader?.Split(';')[0].Trim();

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (Exception) { /* closing a dead socket */ }
        try { _tcp.Dispose(); } catch (Exception) { /* closing a dead socket */ }
    }
}
