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

    private readonly Coalescer _coalescer = new(TimeSpan.FromMilliseconds(50));
    private readonly object _includeLock = new();
    private FileSystemWatcher? _watcher;
    private int _writeCount;
    private int _consecutiveFailures;
    private volatile bool _disposed;

    public string VolumeFilePath { get; }
    public string ConfigTxtPath { get; }
    public int WriteCount => _writeCount;

    /// <summary>
    /// Raised once after 5 consecutive write failures (e.g. apo-volume.txt not writable),
    /// so the UI can surface it. Not raised again until a success resets the streak.
    /// </summary>
    public event Action? WriteFailing;

    public ApoWriter(string configDir)
    {
        VolumeFilePath = Path.Combine(configDir, VolumeFileName);
        ConfigTxtPath = Path.Combine(configDir, "config.txt");
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
    /// directory, so EAPO's own change watcher never reads a half-written file). Same error
    /// contract as the old single-line write: transient failures retry on the next write,
    /// five consecutive failures raise <see cref="WriteFailing"/> once.</summary>
    public void WriteConfig(EqConfigModel model) =>
        _coalescer.Post(() =>
        {
            var temp = VolumeFilePath + ".tmp";
            try
            {
                File.WriteAllText(temp, RenderConfig(model));
                File.Move(temp, VolumeFilePath, overwrite: true);
                Interlocked.Increment(ref _writeCount);
                _consecutiveFailures = 0;
            }
            catch (IOException) { TryDeleteTemp(temp); OnWriteFailed(); }               // transient share violation/AV lock: next write retries
            catch (UnauthorizedAccessException) { TryDeleteTemp(temp); OnWriteFailed(); } // same: graceful degradation, no crash
        });

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

    /// <summary>Synchronous barrier: returns once any pending <see cref="WriteVolume"/> has hit
    /// the file. Lets mode transitions sequence the preamp write against other side effects
    /// (e.g. unmuting the Windows endpoint only after the mute preamp is on disk).</summary>
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
        _disposed = true;
        _watcher?.Dispose();
        lock (_includeLock) { } // wait out any in-flight EnsureInclude
        _coalescer.Dispose();
    }
}
