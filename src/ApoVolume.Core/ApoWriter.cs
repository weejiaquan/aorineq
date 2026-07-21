using System.Globalization;

namespace ApoVolume.Core;

/// <summary>Owns apo-volume.txt in the Equalizer APO config dir and the Include line in config.txt.</summary>
public sealed class ApoWriter : IDisposable
{
    public const string VolumeFileName = "apo-volume.txt";
    public const string IncludeLine = "Include: " + VolumeFileName;

    private readonly Coalescer _coalescer = new(TimeSpan.FromMilliseconds(50));
    private FileSystemWatcher? _watcher;
    private int _writeCount;

    public string VolumeFilePath { get; }
    public string ConfigTxtPath { get; }
    public int WriteCount => _writeCount;

    public ApoWriter(string configDir)
    {
        VolumeFilePath = Path.Combine(configDir, VolumeFileName);
        ConfigTxtPath = Path.Combine(configDir, "config.txt");
    }

    public static string FormatPreamp(double db)
    {
        // Normalize -0.0 to 0.0
        if (db == 0)
            db = 0;
        return string.Create(CultureInfo.InvariantCulture, $"Preamp: {db:0.0} dB");
    }

    public void WriteVolume(double db) =>
        _coalescer.Post(() =>
        {
            File.WriteAllText(VolumeFilePath, FormatPreamp(db) + Environment.NewLine);
            Interlocked.Increment(ref _writeCount);
        });

    public bool EnsureInclude()
    {
        var lines = File.Exists(ConfigTxtPath) ? File.ReadAllLines(ConfigTxtPath) : Array.Empty<string>();
        if (lines.Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)))
            return false;
        File.AppendAllText(ConfigTxtPath, Environment.NewLine + IncludeLine + Environment.NewLine);
        return true;
    }

    public void StartIncludeGuard()
    {
        var dir = Path.GetDirectoryName(ConfigTxtPath)!;
        _watcher = new FileSystemWatcher(dir, "config.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
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
            EnsureInclude();   // no-op when the line is present, so no event loop
        }
        catch (IOException) { }               // file locked mid-write: next event retries
        catch (UnauthorizedAccessException) { } // config.txt not writable: surfaced at startup, not here
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _coalescer.Dispose();
    }
}
