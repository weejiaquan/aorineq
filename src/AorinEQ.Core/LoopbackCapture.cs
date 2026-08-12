using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>WASAPI loopback capture of the default render endpoint's post-mix stream — the
/// signal AFTER Equalizer APO, so meters show the real post-EQ level. Event-driven
/// (AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK, Win10+), raw COM in the repo's
/// EndpointVolume style, no audio NuGet dependencies. Runs ONLY between <see cref="Start"/>
/// and <see cref="Stop"/> — the EQ window opens/closes it, and a stopped capture holds no
/// thread, event handle, or COM object (no idle CPU).
///
/// Error contract: <see cref="Start"/> returns false on any init failure (no device, audio
/// service down, exotic mix format) with everything released; a mid-capture device failure
/// ends the capture thread quietly (the owner re-attaches via <see cref="Restart"/> on a
/// default-device change). <see cref="SamplesAvailable"/> is raised on the capture thread —
/// UI must marshal, same contract as EndpointVolume.Changed.</summary>
public sealed class LoopbackCapture : IDisposable
{
    private const int ClsCtxAll = 0x17;                    // CLSCTX_ALL
    private const int ShareModeShared = 0;                 // AUDCLNT_SHAREMODE_SHARED
    private const int StreamFlagsLoopback = 0x00020000;    // AUDCLNT_STREAMFLAGS_LOOPBACK
    private const int StreamFlagsEventCallback = 0x00040000;
    private const long BufferDuration100ns = 2_000_000;    // 200 ms device buffer
    private const uint BufferFlagsSilent = 0x2;            // AUDCLNT_BUFFERFLAGS_SILENT

    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly object _lock = new();
    private Thread? _thread;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private EventWaitHandle? _event;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Mix-format sample rate while capturing, 0 when stopped.</summary>
    public int SampleRate { get; private set; }

    /// <summary>One captured block as (left, right) float samples, raised on the capture
    /// thread. Mono endpoints duplicate the channel; silent packets arrive as zeros.</summary>
    public event Action<float[], float[]>? SamplesAvailable;

    /// <summary>Attaches to the CURRENT default render endpoint and starts capturing.
    /// False (with everything released) when no endpoint is available or init fails.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_disposed)
                return false;
            if (_running)
                return true;
            try
            {
                return StartLocked();
            }
            catch (COMException)
            {
                StopLocked();
                return false;
            }
            catch (InvalidCastException)
            {
                StopLocked();
                return false;
            }
        }
    }

    /// <summary>Full teardown then a fresh attach — the default-device-change path.</summary>
    public bool Restart()
    {
        Stop();
        return Start();
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            thread = _thread;
            _thread = null;
            _running = false;
            _event?.Set(); // wake the wait so the thread observes _running == false
        }
        thread?.Join();
        lock (_lock)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
        Stop();
    }

    private bool StartLocked()
    {
        var enumerator = (AudioEndpoint.IMMDeviceEnumerator)new AudioEndpoint.MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(
                    AudioEndpoint.EDataFlow.Render, AudioEndpoint.ERole.Multimedia, out var device) < 0
                || device is null)
                return false;
            try
            {
                var iid = typeof(IAudioClient).GUID;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var clientPtr) < 0
                    || clientPtr == IntPtr.Zero)
                    return false;
                _client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
                Marshal.Release(clientPtr);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        if (_client!.GetMixFormat(out var formatPtr) < 0 || formatPtr == IntPtr.Zero)
        {
            StopLocked();
            return false;
        }
        int sampleRate, channels, bits;
        bool isFloat;
        try
        {
            if (ParseMixFormat(formatPtr) is not { } format)
            {
                StopLocked();
                return false;
            }
            (sampleRate, channels, bits, isFloat) = format;
            var session = Guid.Empty;
            if (_client.Initialize(ShareModeShared, StreamFlagsLoopback | StreamFlagsEventCallback,
                    BufferDuration100ns, 0, formatPtr, ref session) < 0)
            {
                StopLocked();
                return false;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPtr);
        }

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        if (_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()) < 0)
        {
            StopLocked();
            return false;
        }

        var captureIid = typeof(IAudioCaptureClient).GUID;
        if (_client.GetService(ref captureIid, out var capturePtr) < 0 || capturePtr == IntPtr.Zero)
        {
            StopLocked();
            return false;
        }
        _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        Marshal.Release(capturePtr);

        if (_client.Start() < 0)
        {
            StopLocked();
            return false;
        }

        SampleRate = sampleRate;
        _running = true;
        var thread = new Thread(() => CaptureLoop(_capture, _event, channels, bits, isFloat))
        {
            IsBackground = true,
            Name = "AorinEQ loopback",
        };
        _thread = thread;
        thread.Start();
        return true;
    }

    /// <summary>Caller must hold <see cref="_lock"/> with the capture thread already gone (or
    /// never started). Releases COM objects, the event handle, and the published rate.</summary>
    private void StopLocked()
    {
        if (_client is not null)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }
        _event?.Dispose();
        _event = null;
        SampleRate = 0;
    }

    /// <summary>Capture thread: wait for the engine's event, drain every ready packet,
    /// convert, raise. Ends on Stop or on any COM failure (device gone — owner Restarts).</summary>
    private void CaptureLoop(IAudioCaptureClient capture, EventWaitHandle ready, int channels,
        int bitsPerSample, bool isFloat)
    {
        try
        {
            while (_running)
            {
                // Timeout keeps the loop responsive to Stop even if the engine goes quiet
                // (loopback fires no events while nothing renders).
                if (!ready.WaitOne(100))
                    continue;
                while (_running)
                {
                    if (capture.GetNextPacketSize(out uint packet) < 0 || packet == 0)
                        break;
                    if (capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _) < 0)
                        return;
                    int releaseHr;
                    try
                    {
                        if (frames > 0)
                            Publish(data, (int)frames, channels, bitsPerSample, isFloat,
                                silent: (flags & BufferFlagsSilent) != 0);
                    }
                    finally
                    {
                        releaseHr = capture.ReleaseBuffer(frames);
                    }
                    if (releaseHr < 0)
                        return;
                }
            }
        }
        catch (COMException) { }              // endpoint died mid-capture: thread ends, Restart re-attaches
        catch (InvalidComObjectException) { } // raced a Stop that released the COM object
        catch (ObjectDisposedException) { }   // raced a Stop that disposed the event handle
    }

    private void Publish(IntPtr data, int frames, int channels, int bitsPerSample, bool isFloat,
        bool silent)
    {
        var left = new float[frames];
        var right = new float[frames];
        if (!silent)
        {
            var raw = new byte[frames * channels * (bitsPerSample / 8)];
            Marshal.Copy(data, raw, 0, raw.Length);
            DecodeInterleaved(raw, frames, channels, bitsPerSample, isFloat, left, right);
        }
        SamplesAvailable?.Invoke(left, right);
    }

    /// <summary>Decodes an interleaved sample block into per-channel floats: float32, or
    /// integer PCM at 16/24 (packed)/32 bits, little-endian. Mono duplicates into both
    /// channels; extra channels beyond the first two are skipped. Public and pure so the
    /// per-format byte math is unit-testable without an audio device.</summary>
    public static void DecodeInterleaved(byte[] raw, int frames, int channels, int bitsPerSample,
        bool isFloat, float[] left, float[] right)
    {
        int bytesPerSample = bitsPerSample / 8;
        int stride = channels * bytesPerSample;
        for (int i = 0; i < frames; i++)
        {
            left[i] = DecodeSample(raw, i * stride, bitsPerSample, isFloat);
            right[i] = channels > 1
                ? DecodeSample(raw, i * stride + bytesPerSample, bitsPerSample, isFloat)
                : left[i];
        }
    }

    private static float DecodeSample(byte[] raw, int offset, int bitsPerSample, bool isFloat)
    {
        if (isFloat)
            return BitConverter.ToSingle(raw, offset);
        return bitsPerSample switch
        {
            16 => BitConverter.ToInt16(raw, offset) / 32768f,
            // 24-bit packed little-endian: sign-extend via a <<8 into an int's top bytes.
            24 => ((raw[offset] << 8 | raw[offset + 1] << 16 | raw[offset + 2] << 24) >> 8)
                / 8388608f,
            _ => BitConverter.ToInt32(raw, offset) / 2147483648f, // 32-bit int PCM
        };
    }

    /// <summary>Reads the fields this capture needs from a WAVEFORMATEX(TENSIBLE) blob:
    /// (rate, channels, bits, isFloat). Accepts float32 and 16/24/32-bit integer PCM — the
    /// shared-mode mix format is float32 in practice, but PCM mixes exist on some drivers.
    /// Null for anything else.</summary>
    private static (int Rate, int Channels, int Bits, bool IsFloat)? ParseMixFormat(IntPtr format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        int channels = (ushort)Marshal.ReadInt16(format, 2);
        int rate = Marshal.ReadInt32(format, 4);
        int bits = (ushort)Marshal.ReadInt16(format, 14);
        if (channels < 1 || rate < 1)
            return null;
        bool isFloat;
        switch (tag)
        {
            case 3: // WAVE_FORMAT_IEEE_FLOAT
                isFloat = true;
                break;
            case 1: // WAVE_FORMAT_PCM
                isFloat = false;
                break;
            case 0xFFFE: // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID at offset 24
                ushort cbSize = (ushort)Marshal.ReadInt16(format, 16);
                if (cbSize < 22)
                    return null;
                var sub = Marshal.PtrToStructure<Guid>(format + 24);
                if (sub == FloatSubtype) isFloat = true;
                else if (sub == PcmSubtype) isFloat = false;
                else return null;
                break;
            default:
                return null;
        }
        if (isFloat && bits != 32)
            return null;
        if (!isFloat && bits is not (16 or 24 or 32))
            return null;
        return (rate, channels, bits, isFloat);
    }

    // [PreserveSig] everywhere — same contract as the rest of the repo's audio interop
    // (real HRESULTs, hr < 0 checks; S_FALSE exists).
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }
}
