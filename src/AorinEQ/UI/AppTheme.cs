using System.Windows.Threading;
using AorinEQ.Core;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AorinEQ.UI;

/// <summary>Keeps the app's Fluent theme following the Windows light/dark setting, live.
///
/// WPF-UI ships its own watcher (<c>SystemThemeWatcher</c>), and using it would mean TWO things in
/// this process deciding what "dark" means: it reads the theme itself, while
/// <see cref="SystemTheme"/> already does — for the tray glyph and the Fluent OSD style, neither of
/// which WPF-UI knows about. Worse, the two do not have to agree: WPF-UI resolves the SHELL theme
/// in places where the app wants the APPS theme. So the watcher is deliberately unused, and this
/// class is the app's single theme mechanism: <see cref="SystemTheme"/> reads, this applies.
///
/// The APPS theme is the input, not the shell theme — these are the app's own windows, not the
/// taskbar. (<see cref="TrayIcon"/> asks for the shell theme for exactly the opposite reason.)
///
/// SHUTDOWN, and the reason every post below is guarded: <see cref="SystemEvents"/> raises on a
/// system thread, and unsubscribing does not stop a callback already running. Posting to a
/// dispatcher that has begun shutting down THROWS, and an exception on a system thread takes the
/// process down mid-teardown — this has bitten the tray icon before (v2.1.2). Same guard, same
/// reason.</summary>
public sealed class AppTheme : IDisposable
{
    /// <summary>Mica where Windows supports it; WPF-UI silently degrades to a plain background on
    /// builds that do not, so no capability check is needed here.</summary>
    public const WindowBackdropType Backdrop = WindowBackdropType.Mica;

    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    /// <summary>Applies the current Windows theme immediately, then follows it. Construct once,
    /// after <c>Application.Current</c> exists and before the first window is shown, so no window
    /// is ever built against the wrong palette.</summary>
    public AppTheme()
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        ApplyCurrent();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>The theme Windows is currently in, by the app's one reader.</summary>
    public static ApplicationTheme Current =>
        SystemTheme.AppsUseLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark;

    /// <summary>Pushes the current Windows theme into WPF-UI's resource dictionaries. <c>true</c>
    /// updates the accent from the system's, which is what makes the chrome pick up a user's
    /// accent colour change without a restart.</summary>
    private static void ApplyCurrent() => ApplicationThemeManager.Apply(Current, Backdrop, updateAccent: true);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark switches arrive as General; accent changes as Color. Both change the palette.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            RequestApply();
    }

    /// <summary>Hops the re-theme onto the UI thread, guarded — see the shutdown note above.</summary>
    private void RequestApply()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(ApplyIfLive));
        }
        catch (InvalidOperationException)
        {
            // The dispatcher began shutting down between the check and the post.
        }
    }

    /// <summary>Guarded again: a post can already be queued on the dispatcher when this is
    /// disposed, and re-theming a torn-down application is at best pointless.</summary>
    private void ApplyIfLive()
    {
        if (_disposed) return;
        ApplyCurrent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _disposed = true;
    }
}
