using System.Text;

namespace AorinEQ.Core.Raop;

/// <summary>The SDP body carried by ANNOUNCE — the session description that tells the receiver
/// what it is about to be sent.</summary>
public static class RaopSdp
{
    /// <summary>Builds the ANNOUNCE body for an unencrypted ALAC session.
    ///
    /// <paramref name="localAddress"/> is the SENDER's own address and appears in BOTH the o=
    /// and c= lines. Putting the receiver's address in c= — the intuitive reading of
    /// "connection address" — makes ANNOUNCE succeed and then SETUP hang forever with no error
    /// at all. Verified against a capture of a sender that works.
    ///
    /// No a=rsaaeskey / a=aesiv lines: the receiver advertises et=0 and accepts an unencrypted
    /// session, which is what lets this sender carry no crypto.</summary>
    public static string Build(long sessionId, string localAddress, int framesPerPacket,
        int sampleRate)
    {
        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append($"o=iTunes {sessionId} 0 IN IP4 {localAddress}\r\n");
        sb.Append("s=iTunes\r\n");
        sb.Append($"c=IN IP4 {localAddress}\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("m=audio 0 RTP/AVP 96\r\n");
        sb.Append("a=rtpmap:96 AppleLossless\r\n");
        // frameLength compatVersion bitDepth pb mb kb channels maxRun maxFrameBytes avgBitRate rate
        sb.Append($"a=fmtp:96 {framesPerPacket} 0 16 40 10 14 2 255 0 0 {sampleRate}\r\n");
        return sb.ToString();
    }
}
