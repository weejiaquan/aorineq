namespace AorinEQ.Core;

/// <summary>The Skin Designer's undo history: whole-document snapshots, which is cheap because a
/// <see cref="SkinDocument"/> is a small immutable value and an edit only ever swaps one.
///
/// Consecutive commits with the same merge key (a drag from mouse-down to mouse-up, one text field
/// until it loses focus, one opening of the colour picker) are ONE step: each replaces the top of
/// the run until <see cref="EndMerge"/>. The dirty flag compares the current document with the
/// one last saved, by value - so undoing back to the saved state is clean again, and undoing past
/// it is dirty.</summary>
public sealed class DesignerHistory
{
    /// <summary>How many steps back undo can go; the oldest are dropped beyond it.</summary>
    public const int Capacity = 200;

    private readonly List<SkinDocument> _undo = [];
    private readonly List<SkinDocument> _redo = [];
    private string? _mergeKey;
    private SkinDocument _saved;

    public DesignerHistory(SkinDocument initial)
    {
        Current = initial;
        _saved = initial;
    }

    public SkinDocument Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Whether the document differs from what was last saved (or loaded).</summary>
    public bool IsDirty => !Current.Equals(_saved);

    /// <summary>Raised after every change to <see cref="Current"/> or to the dirty flag.</summary>
    public event Action? Changed;

    public void Commit(SkinDocument next, string? mergeKey = null)
    {
        if (next.Equals(Current)) return;

        bool merging = mergeKey is not null && mergeKey == _mergeKey && _undo.Count > 0;
        if (merging && next.Equals(_undo[^1]))
        {
            // The run came back to where it started: no step at all, rather than an empty one.
            _undo.RemoveAt(_undo.Count - 1);
            mergeKey = null;
        }
        else if (!merging)
        {
            _undo.Add(Current);
            if (_undo.Count > Capacity) _undo.RemoveAt(0);
        }

        Current = next;
        _redo.Clear();
        _mergeKey = mergeKey;
        Changed?.Invoke();
    }

    /// <summary>Closes the current merge run: the next commit is a new step whatever its key.</summary>
    public void EndMerge() => _mergeKey = null;

    public SkinDocument Undo()
    {
        if (!CanUndo) return Current;
        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _mergeKey = null;
        Changed?.Invoke();
        return Current;
    }

    public SkinDocument Redo()
    {
        if (!CanRedo) return Current;
        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _mergeKey = null;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>The current document is what is on disk now.</summary>
    public void MarkSaved()
    {
        _saved = Current;
        _mergeKey = null;
        Changed?.Invoke();
    }
}
