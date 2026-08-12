using System.Drawing;
using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Turns a volume state into the <see cref="Icon"/> the notification area shows, and owns
/// the OS handles that costs.
///
/// Two hazards shape this class.
///
/// HANDLES. <c>Bitmap.GetHicon</c> creates a USER icon handle that nothing manages for you:
/// <c>Icon.FromHandle</c> wraps it without taking ownership, so disposing that Icon frees nothing.
/// v2.0.1 shipped exactly that and leaked one handle per tray update. Here the handle is kept
/// beside its Icon and destroyed once, in <see cref="Dispose"/>, after the tray has hidden itself —
/// while the shell is still showing an icon, its handle must stay alive.
///
/// REDRAWS. A held volume key fires many changes a second, and drawing plus GetHicon on each would
/// allocate two handles per keypress. Everything is therefore cached by the LOOK it produces —
/// arc count, mute, taskbar theme, pixel size — not by the percent, so the ~33 percentages inside
/// one arc band all return the same instance. The tray assigns that instance to
/// <c>NotifyIcon.Icon</c>, whose setter compares by reference, so an unchanged look doesn't even
/// reach the shell.
///
/// The cache holds ten entries per icon size — four arc levels plus muted, in two themes — and a
/// session sees one size unless the display scaling changes. Entries for a superseded size are
/// deliberately NOT evicted: eviction would destroy handles between the shell being handed a new
/// icon and having drawn it, which is the one way to turn this cache back into the v2.0.1 bug. Ten
/// icon handles per size the machine has ever reported is not a leak worth that risk.
///
/// UI thread only, like the <c>NotifyIcon</c> it feeds — the tray marshals system-event callbacks
/// onto the dispatcher rather than this type taking a lock it would otherwise never contend.</summary>
public sealed class TrayIconRenderer : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Everything that changes the pixels, and nothing that doesn't. Muted collapses the
    /// arc count to 0 before the key is built (<see cref="TrayGlyph.Draw"/> ignores it while
    /// muted), so muting doesn't fill the cache with identical crosses.</summary>
    private readonly record struct GlyphKey(int Arcs, bool Muted, bool LightTaskbar, int SizePx);

    /// <summary>An Icon that does not own its handle, plus the handle it doesn't own.</summary>
    private readonly record struct CachedIcon(Icon Icon, IntPtr Handle);

    private readonly Dictionary<GlyphKey, CachedIcon> _cache = [];
    private bool _disposed;

    /// <summary>The icon for a volume state, drawn on first request and reused after. The returned
    /// Icon belongs to this renderer and stays valid until <see cref="Dispose"/>; callers must not
    /// dispose it.</summary>
    /// <param name="lightTaskbar">The taskbar's theme, not the apps theme.</param>
    /// <param name="sizePx">The shell's current small-icon size (SM_CXSMICON).</param>
    public Icon Get(int percent, bool muted, bool lightTaskbar, int sizePx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(sizePx, 1);

        var key = new GlyphKey(muted ? 0 : TrayGlyph.ArcCount(percent), muted, lightTaskbar, sizePx);
        if (_cache.TryGetValue(key, out var cached))
            return cached.Icon;

        cached = Create(key);
        _cache[key] = cached;
        return cached.Icon;
    }

    private static CachedIcon Create(GlyphKey key)
    {
        using var bmp = TrayGlyph.Draw(key.Arcs, key.Muted, TrayGlyph.GlyphColor(key.LightTaskbar), key.SizePx);
        IntPtr handle = bmp.GetHicon();
        try
        {
            return new CachedIcon(Icon.FromHandle(handle), handle);
        }
        catch
        {
            DestroyIcon(handle);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cached in _cache.Values)
        {
            cached.Icon.Dispose();      // frees the managed wrapper only — it never owned the handle
            DestroyIcon(cached.Handle); // …so the handle is freed here, exactly once
        }
        _cache.Clear();
    }
}
