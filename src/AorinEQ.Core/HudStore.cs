namespace AorinEQ.Core;

/// <summary>Owns the in-memory layout and its coalesced write-back.
///
/// A drag raises a position change on every mouse-move. Writing hud.json per frame would be
/// sixty file replacements a second, so this posts through the same <see cref="Coalescer"/>
/// ApoWriter uses: the first change lands promptly, the rest at most one per interval with
/// latest-wins, and <see cref="Dispose"/> flushes the trailing one rather than dropping the
/// position the user actually let go at.
///
/// Failures are swallowed exactly as the settings save swallows them — a layout that could not be
/// written is worth losing, and is not worth a dialog in front of somebody who was only moving a
/// widget.</summary>
public sealed class HudStore : IDisposable
{
    /// <summary>Long enough that a drag writes a handful of times rather than per frame, short
    /// enough that the file is right almost immediately after the mouse comes up.</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMilliseconds(400);

    private readonly string _path;
    private readonly Coalescer _saver = new(SaveInterval);
    private readonly object _lock = new();
    private HudLayout _layout;

    public HudStore(string path)
    {
        _path = path;
        _layout = HudLayout.Load(path);
    }

    /// <summary>The current layout. Immutable, so a caller can hold one while another thread
    /// replaces it.</summary>
    public HudLayout Layout
    {
        get { lock (_lock) return _layout; }
    }

    /// <summary>Raised after the in-memory layout changes, on the caller's thread — the HUD
    /// rebuilds its windows from this.</summary>
    public event Action<HudLayout>? Changed;

    /// <summary>Applies <paramref name="mutate"/> to the current layout and schedules a save.
    /// Under the lock so two changes arriving together cannot each build on the same stale copy
    /// and lose one of the two.</summary>
    public HudLayout Update(Func<HudLayout, HudLayout> mutate)
    {
        HudLayout next;
        lock (_lock)
        {
            next = mutate(_layout) ?? _layout;
            if (next == _layout) return _layout; // records compare structurally: nothing changed
            _layout = next;
        }
        Save(next);
        Changed?.Invoke(next);
        return next;
    }

    private void Save(HudLayout layout)
    {
        _saver.Post(() =>
        {
            try
            {
                layout.Save(_path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    /// <summary>Runs any pending save now, on the caller's thread. The barrier the shutdown path
    /// and the tests both need.</summary>
    public void Flush() => _saver.Flush();

    public void Dispose() => _saver.Dispose(); // Coalescer.Dispose flushes the trailing write
}
