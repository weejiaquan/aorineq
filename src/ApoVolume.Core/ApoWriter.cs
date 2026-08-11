using System.Globalization;

namespace ApoVolume.Core;

/// <summary>Owns apo-volume.txt in the Equalizer APO config dir and the Include line in config.txt.</summary>
public sealed class ApoWriter : IDisposable
{
    public const string VolumeFileName = "apo-volume.txt";
    public const string IncludeLine = "Include: " + VolumeFileName;

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

    public void WriteVolume(double db) =>
        _coalescer.Post(() =>
        {
            try
            {
                File.WriteAllText(VolumeFilePath, FormatPreamp(db) + Environment.NewLine);
                Interlocked.Increment(ref _writeCount);
                _consecutiveFailures = 0;
            }
            catch (IOException) { OnWriteFailed(); }               // transient share violation/AV lock: next write retries
            catch (UnauthorizedAccessException) { OnWriteFailed(); } // same: graceful degradation, no crash
        });

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
