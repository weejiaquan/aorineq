using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Undo, redo, merging and the dirty flag - the rules a designer user leans on without
/// thinking, pinned with real documents compared by value.</summary>
public class DesignerHistoryTests
{
    private readonly ITestOutputHelper _out;

    public DesignerHistoryTests(ITestOutputHelper output) => _out = output;

    private static SkinDocument Doc(int i) => SkinDocument.New("skin " + i);

    private void Log(DesignerHistory h, string what) =>
        _out.WriteLine($"{what}: current='{h.Current.Name}' canUndo={h.CanUndo} canRedo={h.CanRedo} dirty={h.IsDirty}");

    [Fact]
    public void Commits_undo_and_redo_in_order()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1));
        h.Commit(Doc(2));
        Log(h, "after two commits");
        Assert.Equal(Doc(1), h.Undo());
        Assert.Equal(Doc(0), h.Undo());
        Assert.False(h.CanUndo);
        Assert.Equal(Doc(0), h.Undo()); // nothing left: stays put
        Assert.Equal(Doc(1), h.Redo());
        Assert.Equal(Doc(2), h.Redo());
        Assert.False(h.CanRedo);
        Log(h, "after redoing both");
    }

    [Fact]
    public void A_commit_equal_to_the_current_document_is_a_no_op()
    {
        var h = new DesignerHistory(Doc(0));
        int changed = 0;
        h.Changed += () => changed++;
        h.Commit(SkinDocument.New("skin 0")); // a different instance, the same value
        Log(h, "after an equal commit");
        Assert.False(h.CanUndo);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void The_same_merge_key_replaces_the_top_entry_until_the_run_ends()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1), "drag");
        h.Commit(Doc(2), "drag");
        h.Commit(Doc(3), "drag");
        Log(h, "after a three-step drag");
        Assert.Equal(Doc(0), h.Undo());
        Assert.False(h.CanUndo);

        h.Redo();
        h.EndMerge();
        h.Commit(Doc(4), "drag"); // same key, but the run ended: a new step
        Assert.Equal(Doc(3), h.Undo());
    }

    [Fact]
    public void A_different_key_or_no_key_starts_a_new_entry()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1), "scale");
        h.Commit(Doc(2), "fps");
        h.Commit(Doc(3));
        h.Commit(Doc(4));
        foreach (int expected in new[] { 3, 2, 1, 0 })
        {
            var actual = h.Undo();
            _out.WriteLine($"undo: expected {Doc(expected)}, actual {actual}");
            Assert.Equal(Doc(expected), actual);
        }
    }

    [Fact]
    public void A_merge_run_that_returns_to_where_it_started_leaves_no_empty_step()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1), "drag");
        h.Commit(Doc(0), "drag"); // dragged back to the start
        Log(h, "after dragging back");
        Assert.False(h.CanUndo);
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void A_new_commit_after_undo_clears_redo()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1));
        h.Undo();
        h.Commit(Doc(2));
        Log(h, "after undo then a new commit");
        Assert.False(h.CanRedo);
        Assert.Equal(Doc(0), h.Undo());
    }

    [Fact]
    public void History_keeps_the_last_two_hundred_steps()
    {
        var h = new DesignerHistory(Doc(0));
        for (int i = 1; i <= 250; i++) h.Commit(Doc(i));
        int undone = 0;
        while (h.CanUndo) { h.Undo(); undone++; }
        _out.WriteLine($"undo steps available: {undone}; oldest reachable: '{h.Current.Name}'");
        Assert.Equal(DesignerHistory.Capacity, undone);
        Assert.Equal(Doc(50), h.Current);
    }

    [Fact]
    public void Dirty_follows_the_saved_snapshot()
    {
        var h = new DesignerHistory(Doc(0));
        Assert.False(h.IsDirty);
        h.Commit(Doc(1));
        Assert.True(h.IsDirty);
        h.MarkSaved();
        Assert.False(h.IsDirty);
        h.Commit(Doc(2));
        h.Undo();
        Log(h, "edited then undone back to the saved state");
        Assert.False(h.IsDirty);
    }

    /// <summary>Review focus: save, undo one step - the window must ask before closing; redo back
    /// to what is on disk and it must not.</summary>
    [Fact]
    public void Undoing_past_a_save_is_dirty_and_redoing_to_it_is_clean()
    {
        var h = new DesignerHistory(Doc(0));
        h.Commit(Doc(1));
        h.MarkSaved();
        h.Undo();
        Log(h, "saved at 1, undone to 0");
        Assert.True(h.IsDirty);
        h.Redo();
        Log(h, "redone to 1");
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void Changed_fires_for_every_change_including_a_save()
    {
        var h = new DesignerHistory(Doc(0));
        var events = new List<string>();
        h.Changed += () => events.Add(h.Current.Name + (h.IsDirty ? "*" : ""));
        h.Commit(Doc(1));
        h.Undo();
        h.Redo();
        h.MarkSaved();
        h.Undo(); // fine
        h.Undo(); // nothing to undo: no event
        _out.WriteLine(string.Join(" | ", events));
        Assert.Equal(new[] { "skin 1*", "skin 0", "skin 1*", "skin 1", "skin 0*" }, events);
    }
}
