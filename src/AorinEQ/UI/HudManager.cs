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
    // The two suppression rules are tracked SEPARATELY because they want opposite things from
    // the capture: the silence rule needs it (it is its own sensor), the fullscreen rule must
    // release it (a game is on screen and the HUD is not).
    private bool _hiddenByFullscreen;
    private bool _hiddenBySilence;
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
        if (window.Widget.Z < top)
            _store.Update(l => l.With(window.Widget with { Z = top + 1 }));
        // Restack even when the Z did not change: the widget the user just pressed is the one they
        // want in front, and recording a number without moving a window is what made "bring to
        // front" do nothing at all the first time round.
        ApplyZOrder();
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
        // The two rules pull in opposite directions, so they are answered separately.
        //
        // FULLSCREEN always releases: a game is in front, the HUD is not on screen, and the thing
        // that will bring it back is the foreground window, not the audio. Holding a WASAPI
        // capture open through a whole game for nothing is exactly what this must not do.
        //
        // SILENCE must NOT release, because the capture IS its sensor: let it go and nothing is
        // left that could notice playback resuming, and the widgets never come back. That was a
        // real defect, and re-fixing it as "keep the registration whenever suppressed" quietly
        // reintroduced the fullscreen leak - hence two flags rather than one.
        bool needed = layout.NeedsAudio() && _windows.Count > 0
            && !_hiddenByFullscreen
            && (!_hiddenBySilence || layout.OnlyWhilePlaying);
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
        if (_hiddenByFullscreen || _hiddenBySilence) return;

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
        var layout = _store.Layout;
        bool fullscreen = !EditMode && layout.HideWhenFullscreen && IsFullscreenAppForeground();
        bool silent = !EditMode && layout.OnlyWhilePlaying
            && _clock.Elapsed - _lastSignal > SilenceHold;

        // Coming back from fullscreen with the silence rule on: the capture was released while the
        // game was up, so there is no evidence either way about whether anything is playing now.
        // Treat that as "just heard something" - the widgets come back at once and fade again on
        // their own if it really is silent, which beats staying hidden on stale evidence.
        if (_hiddenByFullscreen && !fullscreen && layout.OnlyWhilePlaying)
        {
            _lastSignal = _clock.Elapsed;
            silent = false;
        }

        bool hide = fullscreen || silent;
        bool wasHidden = _hiddenByFullscreen || _hiddenBySilence;
        bool reasonsChanged = fullscreen != _hiddenByFullscreen || silent != _hiddenBySilence;
        _hiddenByFullscreen = fullscreen;
        _hiddenBySilence = silent;
        if (!force && !reasonsChanged) return;

        if (force || hide != wasHidden)
        {
            foreach (var window in _windows.Values)
            {
                if (hide) window.Hide();
                else window.Show();
            }
            // Show() lifts a window to the top of the topmost band, so unhiding the HUD would
            // otherwise restack it into dictionary order and undo the front-to-back arrangement
            // the user chose.
            if (!hide) ApplyZOrder();
        }
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

        // MonitorFromWindow, NOT the full display enumeration: this runs on the dispatcher on
        // EVERY frame, and DisplayMonitors.Enumerate walks every adapter and every monitor device
        // to build its identity map. Two cheap native calls answer the only question asked here -
        // which screen is this window on, and does it cover the whole of it.
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        return r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
            && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
