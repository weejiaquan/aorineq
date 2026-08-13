using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>The HUD: the widget windows, the layout behind them, and the ONE render loop and ONE
/// audio registration they share.
///
/// THE SHARED-PIPELINE RULE LIVES HERE. There is a single <see cref="DispatcherTimer"/> for the
/// whole HUD, not one per widget, and it takes ONE <see cref="SharedAudioPipeline.Analyze"/>
/// reading per tick and hands the same instance to every widget. It also takes exactly ONE
/// consumer registration on the pipeline — held while any VISIBLE audio-reading widget exists and
/// released the moment the last one goes — so the capture starts and stops with the HUD's need for
/// it and never per window. If this class ever grew a timer or a registration per widget, the CPU
/// measurement is where it would show.
///
/// Everything here runs on the dispatcher thread.</summary>
internal sealed class HudManager : IDisposable
{
    private readonly HudStore _store;
    private readonly SharedAudioPipeline _pipeline;
    private readonly Func<HudRuntimeState> _getState;
    private readonly Dictionary<string, HudWidgetWindow> _windows = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IDisposable? _audio;
    private System.Windows.Controls.ContextMenu? _openMenu;
    private TimeSpan _lastTick;
    private EqPalette _palette = EqPalette.For(SystemTheme.AppsUseLightTheme());
    private SkinInfo? _skin;
    private bool _suppressed;      // hidden by the fullscreen or only-while-playing rules
    private bool _disposed;
    private TimeSpan _lastSignal;

    /// <summary>How long after the last audio the widgets stay up under "show only while
    /// playing". Without a hold, the HUD would blink on every gap between tracks.</summary>
    private static readonly TimeSpan SilenceHold = TimeSpan.FromSeconds(3);

    /// <summary>What the HUD needs from the app each frame. A snapshot function rather than a
    /// pile of references, so the HUD holds no part of the app's state and cannot fall behind it.</summary>
    /// <summary>Deliberately does NOT carry the skin. Resolving a SkinInfo means reading the skin
    /// folder off disk, and this snapshot is taken on the UI thread on every frame — the volume
    /// widget is handed its skin once, by <see cref="SetSkin"/>, when the active skin changes.</summary>
    internal sealed record HudRuntimeState(
        int Percent, bool Muted, double? VolumeDb, string DeviceName,
        IReadOnlyList<EqBand> EqBands);

    /// <summary>Raised when the layout changes for any reason — Settings mirrors the mode.</summary>
    public event Action<HudLayout>? LayoutChanged;

    public HudManager(HudStore store, SharedAudioPipeline pipeline, Func<HudRuntimeState> getState)
    {
        _store = store;
        _pipeline = pipeline;
        _getState = getState;
        _timer.Tick += (_, _) => OnFrame();
        // ApplicationThemeManager.Changed is a STATIC event; Dispose removes this, and the HUD
        // lives for the whole session, so there is no window-lifetime trap here like the one the
        // EQ editor has to guard against.
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;
    }

    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color accent) =>
        ApplyPalette(EqPalette.For(theme == Wpf.Ui.Appearance.ApplicationTheme.Light));

    public HudLayout Layout => _store.Layout;

    public bool EditMode => _store.Layout.Mode == HudModes.Edit;

    /// <summary>Builds (or rebuilds) the widget windows from the current layout and starts the
    /// shared loop. Safe to call repeatedly.</summary>
    public void Apply()
    {
        if (_disposed) return;
        var layout = _store.Layout;

        // Windows for widgets that no longer exist, or are no longer visible, go first — so the
        // audio registration below is decided against what is really on screen.
        foreach (var id in _windows.Keys.ToList())
        {
            var widget = layout.Find(id);
            if (widget is null || !widget.Visible)
                CloseWindow(id);
        }

        foreach (var widget in layout.Widgets.Where(w => w.Visible).OrderBy(w => w.Z))
        {
            if (_windows.TryGetValue(widget.Id, out var existing))
            {
                existing.ApplyWidget(widget);
            }
            else
            {
                var created = CreateWindow(widget);
                if (created is null) continue;
                _windows[widget.Id] = created;
            }
        }

        PlaceAll();
        foreach (var window in _windows.Values)
        {
            window.SetEditMode(EditMode);
            window.ApplyPalette(_palette);
        }
        UpdateTimer();
        // Suppression FIRST, and it ends in UpdateAudioRegistration itself: deciding the
        // registration before knowing whether the widgets are even going to be on screen would
        // start a WASAPI capture and drop it again in the same call.
        ApplySuppression(force: true);
        LayoutChanged?.Invoke(layout);
    }

    private HudWidgetWindow? CreateWindow(HudWidget widget)
    {
        IHudWidgetView view = widget.Type switch
        {
            HudWidgetTypes.Spectrum => new HudSpectrumView(),
            HudWidgetTypes.Levels => new HudLevelsView(),
            HudWidgetTypes.EqCurve => new HudEqCurveView(),
            HudWidgetTypes.Volume => new HudVolumeView(),
            _ => new HudSpectrumView(),
        };
        if (view is HudVolumeView volume)
            volume.SetSkin(_skin);

        var window = new HudWidgetWindow(widget, view);
        window.BoxChanged += OnBoxChanged;
        window.BoxDragging += OnBoxDragging;
        window.SettingsRequested += ShowWidgetMenu;
        window.RemoveRequested += w => Remove(w.Widget.Id);
        window.Pressed += BringToFront;
        window.ApplyPalette(_palette);
        window.Show();       // ShowActivated=false: shows without taking focus
        return window;
    }

    /// <summary>Right-click in edit mode: this widget's own settings, on the widget itself.</summary>
    private void ShowWidgetMenu(HudWidgetWindow window)
    {
        CloseWidgetMenu();
        var menu = HudWidgetMenu.Build(this, window.Widget);
        menu.PlacementTarget = window;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        // A menu over a WS_EX_NOACTIVATE window would otherwise stay open after a click elsewhere,
        // because the window never takes focus to lose.
        menu.StaysOpen = false;
        menu.Closed += (_, _) => { if (ReferenceEquals(_openMenu, menu)) _openMenu = null; };
        _openMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Dismisses an open widget menu.
    ///
    /// This is part of live mode being click-through, not housekeeping. The widget WINDOWS go
    /// click-through the moment the mode changes, but a popup is an ordinary interactive window
    /// of its own — leaving one up would mean the first desktop click after switching to live was
    /// still swallowed, by the one surface the mode switch had not reached.</summary>
    private void CloseWidgetMenu()
    {
        if (_openMenu is null) return;
        _openMenu.IsOpen = false;
        _openMenu = null;
    }

    private void CloseWindow(string id)
    {
        if (!_windows.Remove(id, out var window)) return;
        // The view may hold a skin whose animation timers root it; close through the window so
        // its own teardown runs.
        if (window.View is HudVolumeView volume) volume.SetSkin(null);
        window.Close();
    }

    /// <summary>Re-resolves every widget onto the CURRENT set of screens. Called on start, on any
    /// layout change, and on a display change — which is the moment a widget would otherwise be
    /// left sitting on coordinates that no longer exist.</summary>
    public void PlaceAll()
    {
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count == 0) return;

        bool anyMoved = false;
        foreach (var (id, window) in _windows)
        {
            var widget = _store.Layout.Find(id);
            if (widget is null) continue;
            if (HudPlacement.TryResolve(widget, monitors) is not { } placed) continue;
            window.SetBox(placed.Bounds);
            anyMoved |= placed.MovedToFallback;
        }
        ApplyZOrder();
        if (anyMoved) MovedToPrimary?.Invoke();
    }

    /// <summary>Puts the native windows in the order the layout says. Every widget is topmost, so
    /// "in front" is decided within that band by the order they were last raised — lift them in
    /// ASCENDING Z and the highest ends up last, and therefore in front.
    ///
    /// Without this, "bring to front" changed a number in a file and nothing on screen, and a
    /// widget could sit permanently buried under another one with no way to reach it.</summary>
    private void ApplyZOrder()
    {
        foreach (var widget in _store.Layout.Widgets.OrderBy(w => w.Z))
            if (_windows.TryGetValue(widget.Id, out var window))
                window.BringToTop();
    }

    /// <summary>Raised when at least one widget had to be placed on the primary screen because the
    /// one it remembered is gone. The app balloons it: a widget that moved is news, and a widget
    /// that silently vanished is a bug report.</summary>
    public event Action? MovedToPrimary;

    private HudRect OnBoxDragging(HudWidgetWindow window, HudRect box)
    {
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count == 0) return box;
        // Snap against the work area the box is mostly over, and against every OTHER widget.
        var host = monitors.OrderByDescending(m => box.IntersectionArea(m.WorkArea)).First();
        var others = _windows.Values
            .Where(w => !ReferenceEquals(w, window))
            .Select(w => w.Box)
            .ToList();
        // The threshold is a DISTANCE THE USER PERCEIVES, so it is expressed in DIPs and scaled
        // into the pixel space the boxes live in. Comparing 12 DIPs against pixels directly would
        // make the snap band shrink to two thirds of itself at 150% and to half at 200%, on the
        // very displays where everything else is bigger.
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return HudSnap.Apply(box, host.WorkArea, others,
            HudSnap.DefaultThreshold * (scale > 0 ? scale : 1));
    }

    private void OnBoxChanged(HudWidgetWindow window, HudRect box)
    {
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count == 0) return;
        var stored = HudPlacement.Capture(window.Widget, box, monitors);
        _store.Update(l => l.With(stored));
    }

    private void BringToFront(HudWidgetWindow window)
    {
        var layout = _store.Layout;
        int top = layout.Widgets.Count == 0 ? 0 : layout.Widgets.Max(w => w.Z);
        if (window.Widget.Z >= top) return;
        _store.Update(l => l.With(window.Widget with { Z = top + 1 }));
    }

    // ---- layout mutations ----

    public HudWidget Add(string type)
    {
        var widget = HudWidget.Create(type);
        // A new widget lands where it can be SEEN and grabbed: near the top-left of the work area
        // of whichever screen the pointer is on, offset so a second one does not hide the first.
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count > 0)
        {
            var host = monitors.FirstOrDefault(m => m.IsPrimary);
            if (string.IsNullOrEmpty(host.DeviceId)) host = monitors[0];
            int step = 28 * (_store.Layout.Widgets.Count % 8);
            widget = widget with { MonitorId = host.DeviceId, X = 40 + step, Y = 40 + step };
        }
        _store.Update(l => l.Add(widget));
        Apply();
        return widget;
    }

    public void Remove(string id)
    {
        CloseWindow(id);
        _store.Update(l => l.Remove(id));
        Apply();
    }

    public void Update(HudWidget widget)
    {
        _store.Update(l => l.With(widget));
        Apply();
    }

    public void SetMode(string mode)
    {
        _store.Update(l => l with { Mode = HudModes.Normalize(mode) });
        if (!EditMode) CloseWidgetMenu();
        foreach (var window in _windows.Values) window.SetEditMode(EditMode);
        // Edit mode must be able to reach a widget that the fullscreen or silence rules would
        // otherwise keep hidden — otherwise "arrange my widgets" shows an empty screen.
        ApplySuppression(force: true);
        LayoutChanged?.Invoke(_store.Layout);
    }

    public void SetBehaviour(bool hideWhenFullscreen, bool onlyWhilePlaying, int fps)
    {
        _store.Update(l => l with
        {
            HideWhenFullscreen = hideWhenFullscreen,
            OnlyWhilePlaying = onlyWhilePlaying,
            Fps = Math.Clamp(fps, HudLayout.MinFps, HudLayout.MaxFps),
        });
        UpdateTimer();
        LayoutChanged?.Invoke(_store.Layout);
    }

    public void SetVisible(string id, bool visible)
    {
        if (_store.Layout.Find(id) is not { } widget) return;
        _store.Update(l => l.With(widget with { Visible = visible }));
        Apply();
    }

    /// <summary>The active skin changed (or was cleared). Handed the very SkinInfo the OSD is
    /// using, so both surfaces render the same validated pixels.</summary>
    public void SetSkin(SkinInfo? skin)
    {
        _skin = skin;
        foreach (var window in _windows.Values)
            if (window.View is HudVolumeView volume) volume.SetSkin(skin);
    }

    public void ApplyPalette(EqPalette palette)
    {
        _palette = palette;
        foreach (var window in _windows.Values) window.ApplyPalette(palette);
    }

    /// <summary>The default render device changed: re-attach the shared capture. A no-op when
    /// nobody is registered, which is the pipeline's own rule.</summary>
    public void OnDeviceChanged() => _pipeline.Restart();

    // ---- the one loop ----

    private void UpdateTimer()
    {
        int fps = Math.Clamp(_store.Layout.Fps, HudLayout.MinFps, HudLayout.MaxFps);
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        bool wanted = _windows.Count > 0 && !_disposed;
        if (wanted && !_timer.IsEnabled)
        {
            _lastTick = _clock.Elapsed;
            _timer.Start();
        }
        else if (!wanted && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    /// <summary>ONE registration for the whole HUD, taken while any VISIBLE audio-reading widget
    /// exists. Not one per widget: the signal is identical in all of them, and the capture is a
    /// thread, an event handle and a COM object.</summary>
    private void UpdateAudioRegistration()
    {
        var layout = _store.Layout;
        // "Show only while audio is playing" is the one rule whose SENSOR is the capture itself:
        // release the registration while it is hiding the HUD and nothing is left to notice that
        // playback resumed, so the widgets would never come back. That rule therefore keeps the
        // registration through its own suppression. The fullscreen rule has an independent
        // trigger (the foreground window), so it releases as you would expect.
        bool suppressedButStillListening = _suppressed && layout.OnlyWhilePlaying;
        bool needed = layout.NeedsAudio() && _windows.Count > 0
            && (!_suppressed || suppressedButStillListening);
        if (needed && _audio is null)
            _audio = _pipeline.AddConsumer("HUD");
        else if (!needed && _audio is not null)
        {
            _audio.Dispose();
            _audio = null;
        }
    }

    private void OnFrame()
    {
        if (_disposed || _windows.Count == 0) return;

        var now = _clock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;

        // Taken BEFORE the suppression decision and even while hidden: under "only while playing"
        // this reading is the only thing that can notice playback starting again.
        var analysis = _audio is not null ? _pipeline.Analyze() : null;
        if (analysis is { HasSignal: true }) _lastSignal = now;

        ApplySuppression(force: false);
        if (_suppressed) return;

        var state = _getState();
        var frame = new HudFrame(analysis, elapsed, state.Percent, state.Muted,
            state.VolumeDb, state.DeviceName, state.EqBands);

        foreach (var window in _windows.Values)
        {
            // A widget whose source has not changed is skipped entirely. That is not every
            // widget: a spectrum and a meter are still moving on a frame with no new audio,
            // because their own ballistics are falling. It is what makes the EQ CURVE and the
            // VOLUME widget cost nothing at all between changes, which is most of the difference
            // between four widgets and four times one widget.
            if (window.View.NeedsRedraw(frame))
                window.View.Render(frame);
        }
    }

    /// <summary>The two behaviour rules, applied together because they answer one question: should
    /// the widgets be on screen right now.
    ///
    /// EDIT MODE OVERRIDES BOTH. Arranging widgets you cannot see is not arranging them.</summary>
    private void ApplySuppression(bool force)
    {
        bool hide = false;
        if (!EditMode)
        {
            var layout = _store.Layout;
            if (layout.HideWhenFullscreen && IsFullscreenAppForeground())
                hide = true;
            if (layout.OnlyWhilePlaying && _clock.Elapsed - _lastSignal > SilenceHold)
                hide = true;
        }
        if (!force && hide == _suppressed) return;

        _suppressed = hide;
        foreach (var window in _windows.Values)
        {
            if (hide) window.Hide();
            else window.Show();
        }
        // A hidden HUD must not hold the capture open — that is the whole point of the
        // only-while-playing rule, and it applies to the fullscreen rule for free.
        UpdateAudioRegistration();
    }

    /// <summary>Whether the FOREGROUND window covers a whole monitor and is not the shell.
    ///
    /// Deliberately not QUERY_USER_NOTIFICATION_STATE: that also reports "presentation mode" and
    /// "running a D3D exclusive app" for things this HUD has no quarrel with, and it says nothing
    /// about WHICH monitor. Measuring the foreground window against its own monitor answers the
    /// actual question — is something covering the screen a widget is on. The desktop and the
    /// shell are excluded by name because both are legitimately screen-sized.</summary>
    private static bool IsFullscreenAppForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        var shell = GetShellWindow();
        if (hwnd == shell) return false;
        var cls = new System.Text.StringBuilder(64);
        if (GetClassName(hwnd, cls, cls.Capacity) > 0)
        {
            string name = cls.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        }

        if (!GetWindowRect(hwnd, out var r)) return false;
        var box = new HudRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        // Which screen it is on, from the same enumeration the widgets are placed against, rather
        // than a second source of truth for what a monitor is.
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count == 0) return false;
        var screen = monitors.OrderByDescending(m => box.IntersectionArea(m.Bounds)).First().Bounds;
        return box.X <= screen.X && box.Y <= screen.Y
            && box.Right >= screen.Right && box.Bottom >= screen.Bottom;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
        CloseWidgetMenu();
        _timer.Stop();
        _audio?.Dispose();
        _audio = null;
        foreach (var id in _windows.Keys.ToList()) CloseWindow(id);
        _store.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
