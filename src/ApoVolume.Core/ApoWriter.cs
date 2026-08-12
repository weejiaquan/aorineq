using System.Globalization;
using System.Text;

namespace ApoVolume.Core;

/// <summary>One device's slice of the rendered config: the endpoint GUID for the
/// <c>Device:</c> guard, the volume preamp component (0 in system volume mode — loudness
/// rides the Windows endpoint there), the scope's EQ bypass, and the active preset's own
/// clipping-prevention preamp + bands.</summary>
public sealed record DeviceEqSection(
    string EndpointGuid, double VolumeDb, bool EqEnabled, double PresetPreampDb,
    IReadOnlyList<EqBand> Bands);

/// <summary>Everything <see cref="ApoWriter.RenderConfig"/> needs to produce apo-volume.txt:
/// the global scope (filters only — its preset preamp folds into each device's Preamp line,
/// because EAPO preamps sum in dB and a global Preamp line would double up) and the
/// per-device sections.</summary>
public sealed record EqConfigModel(
    bool GlobalEqEnabled, double GlobalPresetPreampDb, IReadOnlyList<EqBand> GlobalBands,
    IReadOnlyList<DeviceEqSection> Devices);

/// <summary>Owns apo-volume.txt in the Equalizer APO config dir and the Include line in config.txt.</summary>
public sealed class ApoWriter : IDisposable
{
    public const string VolumeFileName = "apo-volume.txt";
    public const string IncludeLine = "Include: " + VolumeFileName;
    public const string ManagedHeader = "# managed by apo-volume - do not hand-edit";

    /// <summary>Retry budget for a failed write, and the delay before the first retry. Each
    /// step doubles, so the attempts land 25, 75, 175, 375 and 775 ms after the first failure:
    /// the last requested state reaches disk within ~0.8 s of a transient lock clearing.
    /// Bounded on purpose — a file that stays unwritable raises <see cref="WriteFailing"/>
    /// rather than retrying forever.</summary>
    private const int MaxWriteRetries = 5;
    private const int FirstRetryDelayMs = 25;

    private readonly Coalescer _coalescer = new(TimeSpan.FromMilliseconds(50));
    private readonly object _includeLock = new();
    private readonly object _retryLock = new();
    private readonly Timer _retryTimer;
    private EqConfigModel? _latestModel;
    // Request stamps, both under _retryLock: _requestSeq counts WriteConfig calls, _writtenSeq
    // records the newest stamp that actually reached disk. _writtenSeq < _requestSeq is exactly
    // "the state the user asked for is not on the file yet", which is what the retry recovers.
    private long _requestSeq;
    private long _writtenSeq;
    private int _retryAttempt;
    private FileSystemWatcher? _watcher;
    private int _writeCount;
    private int _consecutiveFailures;
    private int _disposeGate;
    private volatile bool _disposed;

    public string VolumeFilePath { get; }
    public string ConfigTxtPath { get; }
    public int WriteCount => _writeCount;

    /// <summary>
    /// Raised once after 5 consecutive write failures (e.g. apo-volume.txt not writable),
    /// so the UI can surface it. Retry attempts count toward the streak, so a file that is
    /// genuinely unwritable surfaces within the retry window instead of after five user
    /// actions. Not raised again until a success resets the streak.
    /// </summary>
    public event Action? WriteFailing;

    public ApoWriter(string configDir)
    {
        VolumeFilePath = Path.Combine(configDir, VolumeFileName);
        ConfigTxtPath = Path.Combine(configDir, "config.txt");
        _retryTimer = new Timer(_ => OnRetryDue());
    }

    public static string FormatPreamp(double db)
    {
        // Normalize values that round to -0.0 (e.g. -0.04) so they don't format as "-0.0 dB"
        if (Math.Round(db, 1) == 0.0)
            db = 0.0;
        return string.Create(CultureInfo.InvariantCulture, $"Preamp: {db:0.0} dB");
    }

    /// <summary>Pure renderer for the managed file. Layout: header, global filters (when the
    /// global scope is on), then one GUID-guarded block per device — <c>Device: {guid}</c>
    /// (EAPO substring-matches the braced GUID in its "name connection GUID" device string),
    /// the summed <c>Preamp:</c> line (volume + device preset preamp + global preset preamp;
    /// EAPO sums sequential preamps in dB, and each preset preamp only folds in while its
    /// filters actually render), and the device's filter lines when its scope is on. A trailing
    /// <c>Device: all</c> resets EAPO's device selector so config.txt lines after our Include
    /// can never get accidentally scoped to the last device.</summary>
    public static string RenderConfig(EqConfigModel model)
    {
        var sb = new StringBuilder();
        sb.Append(ManagedHeader).Append(Environment.NewLine);

        bool globalActive = model.GlobalEqEnabled && model.GlobalBands.Count > 0;
        if (globalActive)
        {
            for (int i = 0; i < model.GlobalBands.Count; i++)
                sb.Append(EqPreset.FormatFilterLine(i + 1, model.GlobalBands[i])).Append(Environment.NewLine);
        }

        bool anyDevice = false;
        foreach (var device in model.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.EndpointGuid))
                continue; // no guard string, no block — never emit an unscoped preamp
            anyDevice = true;
            bool deviceActive = device.EqEnabled && device.Bands.Count > 0;
            double preamp = device.VolumeDb
                + (deviceActive ? device.PresetPreampDb : 0)
                + (globalActive ? model.GlobalPresetPreampDb : 0);
            sb.Append("Device: ").Append(device.EndpointGuid).Append(Environment.NewLine);
            sb.Append(FormatPreamp(preamp)).Append(Environment.NewLine);
            if (deviceActive)
            {
                for (int i = 0; i < device.Bands.Count; i++)
                    sb.Append(EqPreset.FormatFilterLine(i + 1, device.Bands[i])).Append(Environment.NewLine);
            }
        }
        if (anyDevice)
            sb.Append("Device: all").Append(Environment.NewLine);
        return sb.ToString();
    }

    /// <summary>The Preamp value inside <paramref name="content"/>'s block for
    /// <paramref name="endpointGuid"/>, or null when the file has no such device block (which
    /// includes the legacy v1.x single-line format). Proof gate for mute handover: callers
    /// verify the device's preamp actually reads what they wrote.</summary>
    public static double? ReadDevicePreamp(string content, string endpointGuid)
    {
        bool inDevice = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
            {
                inDevice = line[7..].Trim().Equals(endpointGuid, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inDevice && line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line[7..].Trim();
                int space = rest.IndexOf(' ');
                var token = space >= 0 ? rest[..space] : rest;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                    return db;
                return null;
            }
        }
        return null;
    }

    /// <summary>Coalesced atomic write of the rendered config (temp file + rename in the same
    /// directory, so EAPO's own change watcher never reads a half-written file). Records
    /// <paramref name="model"/> as the state that must end up on disk, then posts a write.
    /// A failed attempt no longer waits for an unrelated later render to recover it — it
    /// re-arms itself on the backoff ladder (see <see cref="MaxWriteRetries"/>) until the
    /// latest state lands or the budget runs out, at which point five consecutive failures
    /// have raised <see cref="WriteFailing"/> once.</summary>
    public void WriteConfig(EqConfigModel model)
    {
        lock (_retryLock)
        {
            // Shutdown flips _disposed under this same lock, so a request either lands before
            // that (and the drain, which reads this state afterwards, still writes it) or is
            // refused. It can never slip in behind the drain and be left on the floor.
            if (_disposed)
                return;
            _latestModel = model;
            // Every request writes, even one that renders identically: an apo-volume.txt
            // clobbered by another tool must still be repaired by the next render.
            _requestSeq++;
            _retryAttempt = 0; // a fresh request gets the full retry budget
        }
        _coalescer.Post(WriteLatest);
    }

    private void WriteLatest()
    {
        if (!TryWriteLatest())
            ScheduleRetry();
    }

    /// <summary>Writes whatever state was requested LAST, never the payload a retry was
    /// scheduled for — a retry that fires after newer edits must land the newest state, and
    /// coalescing must never resurrect an intermediate one. Runs only on the coalescer (or on
    /// the shutdown drain, once the coalescer is gone), so writes never overlap.</summary>
    /// <returns>true once the last requested state is on disk — including when it already was,
    /// so a retry that outlives the failure it was armed for writes nothing at all; false when
    /// the attempt failed and the state still needs one.</returns>
    private bool TryWriteLatest()
    {
        EqConfigModel model;
        long seq;
        lock (_retryLock)
        {
            if (_latestModel is null || _writtenSeq >= _requestSeq)
                return true;
            model = _latestModel;
            seq = _requestSeq;
        }
        var temp = VolumeFilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, RenderConfig(model));
            File.Move(temp, VolumeFilePath, overwrite: true);
            Interlocked.Increment(ref _writeCount);
            _consecutiveFailures = 0;
            lock (_retryLock)
            {
                if (seq > _writtenSeq)
                    _writtenSeq = seq; // a newer request that arrived mid-write keeps its own write
                _retryAttempt = 0;
                _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); // nothing left to recover
            }
            return true;
        }
        catch (IOException) { TryDeleteTemp(temp); }                // transient share violation/AV lock
        catch (UnauthorizedAccessException) { TryDeleteTemp(temp); } // a reader holds the name: the rename is denied
        OnWriteFailed();
        return false;
    }

    /// <summary>Arms the next attempt on the doubling backoff ladder. Timer-driven, so nothing
    /// spins and no caller thread ever waits on a retry; bounded by
    /// <see cref="MaxWriteRetries"/>, so a permanently unwritable file settles into the
    /// <see cref="WriteFailing"/> path instead of writing forever.</summary>
    private void ScheduleRetry()
    {
        lock (_retryLock)
        {
            if (_disposed || _retryAttempt >= MaxWriteRetries)
                return;
            _retryAttempt++;
            _retryTimer.Change(TimeSpan.FromMilliseconds(FirstRetryDelayMs << (_retryAttempt - 1)),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRetryDue()
    {
        if (_disposed)
            return;
        _coalescer.Post(WriteLatest); // back through the coalescer: never two writes at once
    }

    /// <summary>Runs the ladder synchronously at shutdown. The async retry needs a live timer
    /// and a live process, and neither survives <see cref="Dispose"/> — so the state the user
    /// asked for last gets its attempts here instead, on the same bounded schedule. Costs
    /// nothing on the normal path (the first call returns immediately once the state has
    /// landed) and at most ~0.8 s when the file is genuinely locked as the app exits.</summary>
    private void DrainRetriesBeforeShutdown()
    {
        for (int attempt = 0; !TryWriteLatest(); attempt++)
        {
            if (attempt >= MaxWriteRetries)
                return; // budget spent; WriteFailing has already surfaced this
            Thread.Sleep(FirstRetryDelayMs << attempt);
        }
    }

    private static void TryDeleteTemp(string temp)
    {
        try { File.Delete(temp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnWriteFailed()
    {
        // Actions run one at a time (Coalescer serializes them), so a plain counter is safe.
        _consecutiveFailures++;
        if (_consecutiveFailures == 5)
        {
            WriteFailing?.Invoke();
        }
    }

    /// <summary>Synchronous barrier: returns once any pending <see cref="WriteConfig"/> has had
    /// its attempt at the file. Lets mode transitions sequence the preamp write against other
    /// side effects (e.g. unmuting the Windows endpoint only after the mute preamp is on disk).
    /// A barrier, not a success proof — if that attempt hit a locked file the retry ladder lands
    /// it shortly after, which is why callers that need proof (mute handover) read the file
    /// back rather than trusting the return.</summary>
    public void Flush() => _coalescer.Flush();

    public bool EnsureInclude()
    {
        lock (_includeLock)
        {
            if (_disposed)
                return false;
            var lines = File.Exists(ConfigTxtPath) ? File.ReadAllLines(ConfigTxtPath) : Array.Empty<string>();
            if (lines.Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)))
                return false;
            File.AppendAllText(ConfigTxtPath, Environment.NewLine + IncludeLine + Environment.NewLine);
            return true;
        }
    }

    public void StartIncludeGuard()
    {
        var dir = Path.GetDirectoryName(ConfigTxtPath)!;
        _watcher = new FileSystemWatcher(dir, "config.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            // Max the kernel-side event buffer (64 KB): a burst of writes in the config dir (e.g.
            // Peace saving many files) can overflow the 8 KB default, and an overflow drops the
            // very config.txt event this guard exists to catch.
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnConfigTxtTouched;
        _watcher.Created += OnConfigTxtTouched;
        _watcher.Renamed += (s, e) => OnConfigTxtTouched(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnConfigTxtTouched(object? sender, FileSystemEventArgs e)
    {
        try
        {
            Thread.Sleep(200); // let the external writer (Peace) finish
            if (_disposed)
                return;
            EnsureInclude();   // no-op when the line is present, so no event loop
        }
        catch (IOException) { }               // file locked mid-write: next event retries
        catch (UnauthorizedAccessException) { } // config.txt not writable: surfaced at startup, not here
    }

    public void Dispose()
    {
        // Single-entry teardown: a second caller must not race the drain (duplicate writes
        // through the same temp path) or dispose the retry timer under the first one's feet.
        if (Interlocked.Exchange(ref _disposeGate, 1) != 0)
            return;
        lock (_retryLock) { _disposed = true; } // shuts the WriteConfig gate in the same breath
        _watcher?.Dispose();
        lock (_includeLock) { } // wait out any in-flight EnsureInclude
        // Coalescer first: its Dispose flushes the trailing write, and _disposed already stops
        // that attempt from arming an async retry. The drain then runs the ladder inline, so a
        // write that fails on the way out is still recovered; the timer goes last, because the
        // drain's own successful write disarms it.
        _coalescer.Dispose();
        DrainRetriesBeforeShutdown();
        lock (_retryLock) { _retryTimer.Dispose(); }
    }
}
