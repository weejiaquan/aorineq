namespace AorinEQ.Core;

/// <summary>Hands a protocol link from a short-lived second launch to the running instance: the
/// second launch <see cref="Post"/>s the link to a spool file and signals the existing
/// single-instance show event; the running instance <see cref="TakeAll"/>s on that signal. A
/// named mutex serializes the cross-process file access; the path and mutex name are
/// parameterized for tests only.</summary>
public sealed class ProtocolSpool
{
    public const string DefaultMutexName = "AorinEQ_ProtocolSpool";

    private readonly string _path;
    private readonly string _mutexName;

    public ProtocolSpool(string path, string mutexName = DefaultMutexName)
    {
        _path = path;
        _mutexName = mutexName;
    }

    /// <summary>Default spool location, next to settings.json.</summary>
    public static string DefaultPath => Path.Combine(ApoPaths.GetStateRoot(), "protocol-links.txt");

    /// <summary>Appends one link line. Throws IOException/UnauthorizedAccessException on disk
    /// failure — the posting (second) instance treats that as "nothing delivered".</summary>
    public void Post(string link)
    {
        WithMutex(() =>
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(_path, link + Environment.NewLine);
        });
    }

    /// <summary>Reads and consumes every spooled link (the file is deleted). Returns an empty
    /// list when there is nothing spooled or the spool is unreadable.</summary>
    public IReadOnlyList<string> TakeAll()
    {
        try
        {
            List<string> links = new();
            WithMutex(() =>
            {
                if (!File.Exists(_path)) return;
                links.AddRange(File.ReadAllLines(_path).Where(l => !string.IsNullOrWhiteSpace(l)));
                File.Delete(_path);
            });
            return links;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void WithMutex(Action action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool taken = false;
        try
        {
            try
            {
                taken = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                taken = true; // previous holder died mid-write; the file ops below cope
            }
            if (!taken)
                // Fail CLOSED: never run the file ops unsynchronized against a sibling that's
                // holding the mutex — concurrent append vs read-delete could lose or truncate a
                // link. A throw surfaces as the IOException the callers already handle (Post
                // treats it as "nothing delivered"; TakeAll returns empty).
                throw new IOException("Timed out acquiring the protocol spool lock.");
            action();
        }
        finally
        {
            if (taken) mutex.ReleaseMutex();
        }
    }
}
