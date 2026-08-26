using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AorinEQ.Core.Raop;

/// <summary>One streaming session to one AirPlay receiver: the RTSP handshake, the RTP audio
/// channel, the control channel (sync out, retransmit requests in) and the timing responder.
///
/// THE ORDERING CONSTRAINT. The timing responder and control listener must be RUNNING BEFORE
/// SETUP is sent. The receiver fires NTP timing requests at the sender's timing port during
/// SETUP and will not answer until they are replied to — a socket that is merely bound, with
/// nothing reading it, is not enough. Getting this wrong produces a silent hang after
/// ANNOUNCE returns 200, which looks exactly like the receiver having retired legacy RAOP and
/// is not that at all. This cost most of a day to find; see Connect().
///
/// Error posture matches the rest of the audio layer: nothing here throws at the caller.
/// A failure ends the session, records why, and raises <see cref="Failed"/>.</summary>
public sealed class RaopSession : IDisposable
{
    public const int SampleRate = 44100;

    /// <summary>Packets held for retransmission — about 23 seconds at 352 frames each. Well
    /// beyond any queue depth the sender offers, so a resend request can always be answered
    /// from memory rather than dropped.</summary>
    private const int ResendCapacity = 2048;

    private const int MaxQueuedFrames = SampleRate;      // one second of slack, then drop
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(1);

    private readonly AirPlayDevice _device;
    private readonly int _queueMs;
    private readonly object _gate = new();
    private readonly ResendBuffer _resend = new(ResendCapacity);
    private readonly Stopwatch _clock = new();

    private RtspClient? _rtsp;
    private UdpClient? _audio, _control, _timing;
    private IPEndPoint? _serverAudio, _serverControl;
    private Thread? _sendThread, _timingThread, _controlThread;
    private string _uri = "";

    // The pending-audio ring. Written by the capture thread, drained by the send thread.
    private readonly short[] _ring = new short[MaxQueuedFrames * 2];
    private int _ringHead, _ringTail, _ringCount;

    private uint _ssrc, _startTimestamp;
    private ushort _sequence;
    private volatile bool _running;
    private volatile string _state = "idle";
    private string _serverName = "";
    private string? _lastError;
    private int _receiverLatency;
    private long _packetsSent, _bytesSent, _retransmitRequests, _retransmitsServed;
    private long _retransmitsMissed, _timingReplies, _syncsSent, _silentPackets;

    /// <summary>Raised when the session ends because something went wrong, with a reason
    /// suitable for showing to a person. Raised on a background thread.</summary>
    public event Action<string>? Failed;

    public RaopSession(AirPlayDevice device, int queueMs)
    {
        _device = device;
        _queueMs = queueMs;
    }

    public bool IsStreaming => _running;
    public AirPlayDevice Device => _device;

    private int LatencySamples => Math.Max(0, _queueMs) * SampleRate / 1000;

    /// <summary>Runs the whole handshake and starts streaming. False on any refusal, with
    /// everything released and <see cref="Snapshot"/> carrying the reason.</summary>
    public bool Connect()
    {
        lock (_gate)
        {
            if (_running) return true;
            try
            {
                return ConnectLocked();
            }
            catch (SocketException e) { return Fail($"network error: {e.SocketErrorCode}"); }
            catch (IOException e) { return Fail($"connection lost: {e.Message}"); }
            catch (FormatException e) { return Fail($"unreadable response: {e.Message}"); }
        }
    }

    private bool ConnectLocked()
    {
        _state = "connecting";
        _lastError = null;

        if (!IPAddress.TryParse(_device.Address, out var remote))
            return Fail($"receiver address '{_device.Address}' is not usable");

        _ssrc = (uint)Random.Shared.Next();
        _startTimestamp = (uint)Random.Shared.Next(0, 1 << 30);
        _sequence = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        long sessionId = Random.Shared.NextInt64(1, int.MaxValue);

        _audio = new UdpClient(0, AddressFamily.InterNetwork);
        _control = new UdpClient(0, AddressFamily.InterNetwork);
        _timing = new UdpClient(0, AddressFamily.InterNetwork);
        int controlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
        int timingPort = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;

        // ---------------------------------------------------------------------------------
        // Listeners BEFORE the handshake. See the type comment: the receiver probes the timing
        // port during SETUP and blocks until it is answered. Moving these below SETUP makes the
        // handshake hang forever with no error.
        // ---------------------------------------------------------------------------------
        _running = true;
        StartTimingResponder();
        StartControlListener();

        _rtsp = new RtspClient(remote.ToString(), _device.Port);
        string local = _rtsp.LocalAddress;
        _uri = $"rtsp://{local}/{sessionId}";

        var options = _rtsp.Send("OPTIONS", "*");
        if (!options.Ok) return Fail($"OPTIONS refused ({options.Status})");
        _serverName = options.Header("server") ?? "";

        var sdp = Encoding.ASCII.GetBytes(
            RaopSdp.Build(sessionId, local, AlacFrame.FramesPerPacket, SampleRate));
        var announce = _rtsp.Send("ANNOUNCE", _uri, contentType: "application/sdp", body: sdp);
        if (!announce.Ok)
            return Fail($"ANNOUNCE refused ({announce.Status} {announce.Reason}) — the receiver "
                      + "rejected an unencrypted session");

        var setup = _rtsp.Send("SETUP", _uri, new Dictionary<string, string>
        {
            ["Transport"] = "RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;"
                          + $"control_port={controlPort};timing_port={timingPort}",
        });
        if (!setup.Ok) return Fail($"SETUP refused ({setup.Status} {setup.Reason})");

        _rtsp.SessionId = RtspClient.ParseSessionId(setup.Header("session"));
        var transport = RtspClient.ParseTransport(setup.Header("transport") ?? "");
        if (!transport.TryGetValue("server_port", out var serverPortText)
            || !int.TryParse(serverPortText, out int serverPort))
            return Fail("SETUP response carried no server_port");

        _serverAudio = new IPEndPoint(remote, serverPort);
        // The receiver reports timing_port=0 while still sending timing requests, so that field
        // is never trusted. control_port is real and is where sync packets go.
        _serverControl = new IPEndPoint(remote,
            transport.TryGetValue("control_port", out var cp) && int.TryParse(cp, out int cport)
                ? cport : controlPort);

        var record = _rtsp.Send("RECORD", _uri, new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_sequence};rtptime={_startTimestamp}",
        });
        if (!record.Ok) return Fail($"RECORD refused ({record.Status} {record.Reason})");

        if (int.TryParse(record.Header("audio-latency"), out int latency))
            _receiverLatency = latency;

        _state = "streaming";
        _clock.Restart();
        SendSync(first: true);

        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = "AorinEQ AirPlay send",
            // The RTP clock is unforgiving: a late packet is a dropout. This thread does almost
            // nothing but pack and send, so raising it is cheap and audibly worthwhile.
            Priority = ThreadPriority.AboveNormal,
        };
        _sendThread.Start();
        return true;
    }

    /// <summary>Hands audio to the session. Non-blocking: if the sender has fallen behind, the
    /// OLDEST audio is dropped rather than the newest, because a stream that plays a growing
    /// delay is worse than one that skips.</summary>
    public void PushSamples(ReadOnlySpan<short> interleaved, int frames)
    {
        if (!_running) return;
        int samples = Math.Min(frames * 2, interleaved.Length);
        lock (_ring)
        {
            for (int i = 0; i < samples; i++)
            {
                if (_ringCount == _ring.Length)
                {
                    _ringHead = (_ringHead + 1) % _ring.Length;
                    _ringCount--;
                }
                _ring[_ringTail] = interleaved[i];
                _ringTail = (_ringTail + 1) % _ring.Length;
                _ringCount++;
            }
        }
    }

    /// <summary>Fills one packet's worth, padding with silence when the source has not kept up.
    /// Returns true if any real audio was present.</summary>
    private bool TakeSamples(short[] destination)
    {
        int want = destination.Length;
        lock (_ring)
        {
            int available = Math.Min(want, _ringCount);
            for (int i = 0; i < available; i++)
            {
                destination[i] = _ring[_ringHead];
                _ringHead = (_ringHead + 1) % _ring.Length;
            }
            _ringCount -= available;
            Array.Clear(destination, available, want - available);
            return available > 0;
        }
    }

    public void SetVolume(double db)
    {
        lock (_gate)
        {
            if (_rtsp is null || !_running) return;
            try
            {
                string body = "volume: "
                    + db.ToString("F6", CultureInfo.InvariantCulture) + "\r\n";
                _rtsp.Send("SET_PARAMETER", _uri, contentType: "text/parameters",
                    body: Encoding.ASCII.GetBytes(body));
            }
            catch (SocketException) { /* the send loop will notice and fail the session */ }
            catch (IOException) { }
        }
    }

    // ---- channels ------------------------------------------------------------------------

    private void SendLoop()
    {
        var pcm = new short[AlacFrame.FramesPerPacket * 2];
        var payload = new byte[AlacFrame.MaxPayloadBytes(AlacFrame.FramesPerPacket)];
        var packet = new byte[RtpPacket.HeaderBytes + payload.Length];
        var lastSync = TimeSpan.Zero;
        long index = 0;

        try
        {
            while (_running)
            {
                double due = index * (double)AlacFrame.FramesPerPacket / SampleRate;
                double now = _clock.Elapsed.TotalSeconds;
                if (now < due)
                {
                    // Sleep in small slices: the packet interval is 8 ms and oversleeping is a
                    // dropout, but spinning would burn a core for an idle tray app.
                    Thread.Sleep(Math.Max(1, Math.Min(4, (int)((due - now) * 1000))));
                    continue;
                }

                if (!TakeSamples(pcm))
                    Interlocked.Increment(ref _silentPackets);

                int payloadLength = AlacFrame.PackUncompressed(pcm, AlacFrame.FramesPerPacket, payload);
                uint timestamp = _startTimestamp + (uint)(index * AlacFrame.FramesPerPacket);
                RtpPacket.WriteAudioHeader(packet, _sequence, timestamp, _ssrc, first: index == 0);
                payload.AsSpan(0, payloadLength).CopyTo(packet.AsSpan(RtpPacket.HeaderBytes));
                int total = RtpPacket.HeaderBytes + payloadLength;

                _resend.Store(_sequence, packet.AsSpan(0, total));
                _audio!.Send(packet, total, _serverAudio);

                _sequence++;
                index++;
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, total);

                if (_clock.Elapsed - lastSync >= SyncInterval)
                {
                    SendSync(first: false);
                    lastSync = _clock.Elapsed;
                }
            }
        }
        catch (SocketException e) { FailAsync($"audio send failed: {e.SocketErrorCode}"); }
        catch (ObjectDisposedException) { /* Disconnect raced the loop */ }
    }

    private void SendSync(bool first)
    {
        if (_control is null || _serverControl is null) return;
        try
        {
            uint rtpNow = _startTimestamp + (uint)(_clock.Elapsed.TotalSeconds * SampleRate);
            var packet = new byte[RtpPacket.SyncBytes];
            RtpPacket.WriteSync(packet, rtpNow, rtpNow - (uint)LatencySamples, NtpTime.Now(), first);
            _control.Send(packet, packet.Length, _serverControl);
            Interlocked.Increment(ref _syncsSent);
        }
        catch (SocketException) { /* the send loop reports the failure */ }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Answers the receiver's NTP timing requests. Runs from before SETUP until
    /// teardown — see the type comment for why the "before" matters.</summary>
    private void StartTimingResponder()
    {
        _timingThread = new Thread(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var reply = new byte[RtpPacket.TimingReplyBytes];
            while (_running)
            {
                try
                {
                    var request = _timing!.Receive(ref from);
                    ulong received = NtpTime.Now();
                    int n = RtpPacket.WriteTimingReply(reply, request, received, NtpTime.Now());
                    if (n > 0)
                    {
                        _timing.Send(reply, n, from);
                        Interlocked.Increment(ref _timingReplies);
                    }
                }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "AorinEQ AirPlay timing" };
        _timingThread.Start();
    }

    /// <summary>Serves retransmit requests from the resend buffer. Without this, roughly one
    /// packet in twenty goes permanently missing on a real network and is heard as a dropout.</summary>
    private void StartControlListener()
    {
        _controlThread = new Thread(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    var packet = _control!.Receive(ref from);
                    if (!RtpPacket.ParseRetransmitRequest(packet, out ushort seq, out ushort count))
                        continue;

                    Interlocked.Increment(ref _retransmitRequests);
                    for (int i = 0; i < count; i++)
                    {
                        ushort wanted = (ushort)(seq + i);
                        if (_resend.TryGet(wanted, out var stored))
                        {
                            _audio!.Send(stored, stored.Length, _serverAudio);
                            Interlocked.Increment(ref _retransmitsServed);
                        }
                        else
                        {
                            Interlocked.Increment(ref _retransmitsMissed);
                        }
                    }
                }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "AorinEQ AirPlay control" };
        _controlThread.Start();
    }

    // ---- lifecycle -----------------------------------------------------------------------

    public RaopDiagnostics Snapshot()
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        long bytes = Interlocked.Read(ref _bytesSent);
        return new RaopDiagnostics(
            State: _state,
            Receiver: _device.DisplayName,
            ServerName: _serverName,
            Codec: _state == "streaming" ? "ALAC uncompressed, 16-bit stereo" : "",
            SampleRate: _state == "streaming" ? SampleRate : 0,
            FramesPerPacket: AlacFrame.FramesPerPacket,
            QueueMs: _queueMs,
            ReceiverLatencySamples: _receiverLatency,
            PacketsSent: Interlocked.Read(ref _packetsSent),
            BytesSent: bytes,
            Kbps: seconds > 0 ? bytes * 8 / 1000.0 / seconds : 0,
            RetransmitRequests: Interlocked.Read(ref _retransmitRequests),
            RetransmitsServed: Interlocked.Read(ref _retransmitsServed),
            RetransmitsMissed: Interlocked.Read(ref _retransmitsMissed),
            TimingReplies: Interlocked.Read(ref _timingReplies),
            SyncsSent: Interlocked.Read(ref _syncsSent),
            SilentPackets: Interlocked.Read(ref _silentPackets),
            LastError: _lastError);
    }

    private bool Fail(string reason)
    {
        _lastError = reason;
        _state = "failed";
        Teardown();
        return false;
    }

    private void FailAsync(string reason)
    {
        _lastError = reason;
        _state = "failed";
        _running = false;
        Failed?.Invoke(reason);
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (!_running && _rtsp is null) return;
            _state = "idle";
            Teardown();
        }
    }

    /// <summary>Stops the threads, says TEARDOWN if the connection still works, and releases
    /// everything. Safe to call twice.</summary>
    private void Teardown()
    {
        _running = false;

        try
        {
            if (_rtsp is not null && _uri.Length > 0)
                _rtsp.Send("TEARDOWN", _uri);
        }
        catch (SocketException) { /* already gone */ }
        catch (IOException) { }

        // Disposing the sockets is what wakes the blocking Receive calls in the listener
        // threads; they exit on ObjectDisposedException.
        _audio?.Dispose();
        _control?.Dispose();
        _timing?.Dispose();
        _audio = _control = _timing = null;

        JoinBriefly(_sendThread);
        JoinBriefly(_timingThread);
        JoinBriefly(_controlThread);
        _sendThread = _timingThread = _controlThread = null;

        _rtsp?.Dispose();
        _rtsp = null;
        _clock.Stop();

        lock (_ring)
        {
            _ringHead = _ringTail = _ringCount = 0;
        }
    }

    /// <summary>Threads are background and every blocking call has been woken by a socket
    /// dispose, so a bounded join is enough. A stuck thread must not hang the UI.</summary>
    private static void JoinBriefly(Thread? thread)
    {
        if (thread is null || !thread.IsAlive) return;
        thread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Disconnect();
}
