using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using AorinEQ.Core;
using AorinEQ.Core.Raop;
using Microsoft.Win32;

namespace AorinEQ.UI;

/// <summary>WinForms NotifyIcon wrapper: the dynamic volume glyph, state tooltip, context menu.
///
/// The icon is drawn at runtime by <see cref="TrayIconRenderer"/> from three live inputs — the
/// volume state, the taskbar's theme, and the shell's small-icon size — so it tracks the volume
/// the way the Windows volume icon does, stays legible when the taskbar switches between light and
/// dark, and stays crisp when the DPI changes. Theme and size are re-read on every update (both are
/// a single registry/metric read, the same call OsdWindow already makes per keypress) and the
/// renderer's cache keeps that from redrawing anything.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly TrayIconRenderer _renderer;
    // Windows raises UserPreferenceChanged/DisplaySettingsChanged on its own thread, while both
    // NotifyIcon and the renderer are affine to the thread that built them.
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private int _percent;
    private bool _muted;
    private bool _disposed;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _eqPresetMenu;
    private readonly ToolStripMenuItem _hudMenu;
    private readonly ToolStripMenuItem _airPlayMenu;
    private readonly ToolStripMenuItem _airPlayRefreshItem;
    private readonly ToolStripMenuItem _airPlayDisconnectItem;
    private readonly Dictionary<ToolStripMenuItem, string> _airPlayItemIds = new();
    private readonly ToolStripMenuItem _hudEditItem;
    private readonly ToolStripMenuItem _hudAddMenu;
    // NotifyIcon does not own its ContextMenuStrip, so the tray disposes it itself.
    private readonly ContextMenuStrip _menu;
    private Action? _balloonClickAction;
    private string _leftClick = TrayActions.VolumeBar;
    private string _middleClick = TrayActions.Mute;
    // Screen position of the last mouse move the shell forwarded to the icon — see IsOverIcon.
    private Point? _lastHoverPoint;

    /// <summary>A bindable action was asked for, by <see cref="TrayActions"/> name. Both the
    /// context menu and the mouse buttons come through here, so the app has one switch rather
    /// than one event per action plus a parallel mapping for the buttons.</summary>
    public event Action<string>? ActionRequested;

    public event Action? ExitRequested;

    /// <summary>An EQ preset was picked from the tray submenu, by name.</summary>
    public event Action<string>? EqPresetSelected;

    /// <summary>The HUD's edit/live switch was toggled; true means EDIT.</summary>
    public event Action<bool>? HudModeToggled;

    /// <summary>A HUD widget's visibility was toggled, by widget id.</summary>
    public event Action<string>? HudWidgetToggled;

    /// <summary>A new HUD widget was asked for, by type.</summary>
    public event Action<string>? HudWidgetAdded;

    /// <summary>Raised right before the context menu opens — the app refreshes the EQ preset
    /// submenu here so it always shows the current preset files and active selection.</summary>
    public event Action? MenuOpening;

    /// <summary>A discovered AirPlay receiver was chosen from the tray — its device id.</summary>
    public event Action<string>? AirPlayDeviceChosen;

    public event Action? AirPlayDisconnectRequested;

    /// <summary>The user asked the tray to rescan. Discovery takes a few seconds, so it is never
    /// run just because the menu opened.</summary>
    public event Action? AirPlayRefreshRequested;

    /// <summary>Construction takes native resources — the renderer's first icon handle, then the
    /// shell's notification-area entry — and creating the NotifyIcon can fail (Win32Exception if
    /// the shell isn't accepting icons). Everything is therefore unwound on the way out, so a
    /// tray that fails to appear doesn't strand an icon handle behind it.</summary>
    public TrayIcon()
    {
        _renderer = new TrayIconRenderer();
        NotifyIcon? icon = null;
        ContextMenuStrip? menu = null;
        try
        {
            _muteItem = Localised("tray.menu.mute", (_, _) => ActionRequested?.Invoke(TrayActions.Mute));
            _eqPresetMenu = Localised("tray.menu.eq-preset");

            // "Arrange widgets" rather than "Edit mode": the switch exists so the widgets can be
            // moved, and naming it after the mode would make the user work out what the mode does.
            _hudEditItem = Localised("tray.menu.arrange-widgets",
                (_, _) => HudModeToggled?.Invoke(!_hudEditItem!.Checked));
            _hudAddMenu = Localised("tray.menu.add-widget");
            foreach (var type in HudWidgetTypes.All)
            {
                var chosen = type;
                var item = Localised($"hud.widget.{chosen}.name", (_, _) => HudWidgetAdded?.Invoke(chosen));
                // Four bare nouns otherwise. The hover text says what each widget actually shows.
                HelpDecorator.Apply(item, $"hud.widget.{chosen}");
                _hudAddMenu.DropDownItems.Add(item);
            }
            _hudMenu = Localised("tray.menu.hud");

            _airPlayRefreshItem = Localised("tray.menu.airplay-search",
                (_, _) => AirPlayRefreshRequested?.Invoke());
            _airPlayDisconnectItem = Localised("tray.menu.airplay-disconnect",
                (_, _) => AirPlayDisconnectRequested?.Invoke());
            _airPlayDisconnectItem.Visible = false;
            _airPlayMenu = Localised("tray.menu.airplay");
            _airPlayMenu.DropDownItems.Add(_airPlayRefreshItem);
            _airPlayMenu.DropDownItems.Add(_airPlayDisconnectItem);

            menu = new ContextMenuStrip();
            menu.Items.Add(Localised("tray.menu.volume-bar",
                (_, _) => ActionRequested?.Invoke(TrayActions.VolumeBar)));
            menu.Items.Add(_muteItem);
            menu.Items.Add(Localised("tray.menu.equalizer",
                (_, _) => ActionRequested?.Invoke(TrayActions.Equalizer)));
            menu.Items.Add(_eqPresetMenu);
            menu.Items.Add(_airPlayMenu);
            menu.Items.Add(_hudMenu);
            menu.Items.Add(Localised("tray.menu.settings",
                (_, _) => ActionRequested?.Invoke(TrayActions.Settings)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Localised("tray.menu.exit", (_, _) => ExitRequested?.Invoke()));
            menu.Opening += (_, _) => MenuOpening?.Invoke();

            // Bound XAML strings repaint themselves through LocSource. These are WinForms
            // properties, so they have to be re-set by hand - hence the key list.
            Loc.LanguageChanged += Retranslate;

            icon = new NotifyIcon
            {
                Icon = CurrentIcon(),
                Text = "AorinEQ",
                Visible = true,
                ContextMenuStrip = menu,
            };
            icon.MouseClick += (_, e) =>
            {
                // Right is the context menu, which NotifyIcon shows itself. There is deliberately
                // no double-click binding: WinForms raises MouseClick before MouseDoubleClick, so
                // giving the two different actions would mean delaying every single click by the
                // double-click timeout — making the common action feel broken to serve a rare one.
                var action = e.Button switch
                {
                    MouseButtons.Left => _leftClick,
                    MouseButtons.Middle => _middleClick,
                    _ => TrayActions.None,
                };
                if (action != TrayActions.None) ActionRequested?.Invoke(action);
            };
            // The shell forwards mouse moves to an icon's owner even though it never forwards the
            // wheel — which is exactly what makes IsOverIcon possible.
            icon.MouseMove += (_, _) => _lastHoverPoint = Cursor.Position;
            // One handler for the lifetime of the icon; each balloon sets (or clears) the action so
            // a click on a stale balloon can never fire a newer balloon's action.
            icon.BalloonTipClicked += (_, _) => _balloonClickAction?.Invoke();
            icon.BalloonTipClosed += (_, _) => _balloonClickAction = null;
            _icon = icon;
            _menu = menu;

            // The glyph is theme- and size-dependent, and both can change while the app sits idle:
            // switching Windows to dark mode, or moving the taskbar to a monitor at another DPI.
            // Both are re-read by CurrentIcon, so both handlers just re-apply. Subscribed last, so
            // a callback can never land on a half-built tray.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch
        {
            icon?.Dispose();
            menu?.Dispose();
            _renderer.Dispose();
            throw;
        }
    }

    /// <summary>Which action each mouse button runs. Applied at startup and again whenever the
    /// user changes it in Settings.</summary>
    public void ApplyConfig(Settings s)
    {
        _leftClick = s.TrayLeftClick;
        _middleClick = s.TrayMiddleClick;
    }

    /// <summary>Whether a screen point is over this icon — the gate the wheel hook asks before it
    /// claims a notch.
    ///
    /// There is no clean way to ask the shell. <c>Shell_NotifyIconGetRect</c> would answer exactly,
    /// but it needs the icon's window handle and id, which WinForms keeps private: reaching them
    /// means reflecting into <see cref="NotifyIcon"/>'s internals, which change between .NET
    /// releases and would fail silently on an upgrade — turning a supported API into a landmine.
    ///
    /// So the answer comes from the last mouse move the shell forwarded to us, which it only ever
    /// does while the cursor is ON the icon. A point within one icon's width of that is over it;
    /// the tolerance is the icon size itself, the tightest bound that can never reject a genuine
    /// hover (the recorded point could be at one edge and the cursor at the other). It works the
    /// same when the icon is in the overflow flyout, since the shell forwards moves there too.
    ///
    /// The known cost: leave the icon quickly and scroll within an icon's width of where you left,
    /// and this still says yes. That range is the neighbouring icon, so the worst case is a notch
    /// of our volume instead of nothing at all.</summary>
    public bool IsOverIcon(int x, int y)
    {
        if (_lastHoverPoint is not { } p) return false; // never hovered: nothing to compare against
        var size = SystemInformation.SmallIconSize;
        return Math.Abs(x - p.X) <= size.Width && Math.Abs(y - p.Y) <= size.Height;
    }

    /// <summary>Every menu item whose text comes from the string table, with the key it came from.
    ///
    /// WinForms has no binding equivalent to LocSource, so a language switch has to walk this list
    /// and re-set each Text. Keeping the key beside the item is what makes that possible without a
    /// second, hand-maintained list that would drift the first time an item was renamed.</summary>
    private readonly List<(ToolStripMenuItem Item, string Key)> _localisedItems = [];

    /// <summary>Creates a menu item whose text is looked up now and re-looked-up on every language
    /// change.</summary>
    private ToolStripMenuItem Localised(string key, EventHandler? onClick = null)
    {
        var item = new ToolStripMenuItem(Loc.T(key), null, onClick);
        _localisedItems.Add((item, key));
        return item;
    }

    /// <summary>Re-applies every string this type sets from code. Called on a language change.</summary>
    private void Retranslate()
    {
        foreach (var (item, key) in _localisedItems)
            item.Text = Loc.T(key);

        Update(_percent, _muted);   // the icon tooltip is built from the table too
    }

    /// <summary>Volume state is visible three ways: the glyph itself (arc count, or a cross while
    /// muted), the tooltip, and the checked "Mute" menu item. The glyph is the only one visible
    /// without hovering or opening the menu.
    ///
    /// Called once per volume keypress, so nothing here may allocate: the renderer's cache returns
    /// the same Icon instance for every percent in an arc band, and NotifyIcon's setter compares by
    /// reference, so an unchanged glyph never reaches the shell.</summary>
    public void Update(int percent, bool muted)
    {
        _percent = percent;
        _muted = muted;
        _icon.Text = muted ? Loc.T("app.tray.tooltip.muted") : Loc.T("app.tray.tooltip.level", percent);
        _icon.Icon = CurrentIcon();
        _muteItem.Checked = muted;
    }

    /// <summary>The glyph for the current state, at the shell's current small-icon size
    /// (SystemInformation.SmallIconSize is GetSystemMetrics(SM_CXSMICON/SM_CYSMICON), which is what
    /// the notification area actually asks for — assuming 16px is blurry at 125% and up) and in the
    /// taskbar's current theme.</summary>
    private Icon CurrentIcon() => _renderer.Get(
        _percent, _muted, SystemTheme.SystemUsesLightTheme(), SystemInformation.SmallIconSize.Width);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark switches arrive as General; accent/colour changes as Color.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            RequestIconRefresh();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RequestIconRefresh();

    /// <summary>Hops the refresh onto the UI thread. Unsubscribing in <see cref="Dispose"/> does
    /// not stop a callback that is already running, so the post itself is guarded: posting to a
    /// dispatcher that has begun shutting down throws, and this runs on a system thread where an
    /// exception would take the process down during teardown.</summary>
    private void RequestIconRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(RefreshIcon));
        }
        catch (InvalidOperationException)
        {
            // The dispatcher began shutting down between the check and the post.
        }
    }

    /// <summary>Re-applies the glyph after a theme or DPI change. Guarded again because a callback
    /// can already be queued on the dispatcher when the tray is disposed.</summary>
    private void RefreshIcon()
    {
        if (_disposed) return;
        _icon.Icon = CurrentIcon();
    }

    /// <summary>Rebuilds the "EQ preset" submenu for the active device's scope: one checkable
    /// item per preset file, the active one checked. Empty list disables the submenu.</summary>
    public void SetEqPresets(IReadOnlyList<string> names, string activeName)
    {
        // Rebuilt every time the menu opens, so the discarded items add up over a session:
        // DropDownItems.Clear() only unparents them. Disposed after the Clear, never before —
        // an item is still owned by the collection until it is removed.
        var discarded = _eqPresetMenu.DropDownItems.Cast<ToolStripItem>().ToArray();
        _eqPresetMenu.DropDownItems.Clear();
        foreach (var item in discarded) item.Dispose();

        _eqPresetMenu.Enabled = names.Count > 0;
        foreach (var name in names)
        {
            var item = new ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase),
            };
            var chosen = name;
            item.Click += (_, _) => EqPresetSelected?.Invoke(chosen);
            _eqPresetMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>Rebuilds the AirPlay submenu: one checkable item per discovered receiver, the
    /// connected one checked, above the persistent Search and Disconnect items.
    ///
    /// Rebuilt on demand rather than on every menu open, unlike the EQ presets: discovery costs
    /// seconds of listening on the network and must never be triggered just because somebody
    /// right-clicked the tray.</summary>
    public void SetAirPlayDevices(IReadOnlyList<AirPlayDevice> devices, string? connectedId)
    {
        // DropDownItems.Clear() only unparents; discarded items are disposed after the Clear,
        // never before — an item is still owned by the collection until it is removed. The two
        // items this class owns for the tray's whole life are kept.
        var discarded = _airPlayMenu.DropDownItems.Cast<ToolStripItem>()
            .Where(i => !ReferenceEquals(i, _airPlayRefreshItem)
                     && !ReferenceEquals(i, _airPlayDisconnectItem))
            .ToArray();
        _airPlayMenu.DropDownItems.Clear();
        foreach (var item in discarded) item.Dispose();
        _airPlayItemIds.Clear();

        foreach (var device in devices)
        {
            var item = new ToolStripMenuItem(device.DisplayName)
            {
                Checked = device.Id == connectedId,
            };
            string chosen = device.Id;
            item.Click += (_, _) => AirPlayDeviceChosen?.Invoke(chosen);
            _airPlayMenu.DropDownItems.Add(item);
            // The menu shows room names; identity is the mDNS instance name. Kept side by side
            // so the tick can be moved later without matching on display text.
            _airPlayItemIds[item] = device.Id;
        }

        if (devices.Count > 0)
            _airPlayMenu.DropDownItems.Add(new ToolStripSeparator());
        _airPlayMenu.DropDownItems.Add(_airPlayRefreshItem);
        _airPlayMenu.DropDownItems.Add(_airPlayDisconnectItem);
    }

    /// <summary>Shows or hides Disconnect and moves the tick to whichever receiver is live.</summary>
    public void SetAirPlayStreaming(bool streaming, string? connectedId)
    {
        _airPlayDisconnectItem.Visible = streaming;
        foreach (var item in _airPlayMenu.DropDownItems.OfType<ToolStripMenuItem>())
        {
            if (ReferenceEquals(item, _airPlayRefreshItem)
                || ReferenceEquals(item, _airPlayDisconnectItem)) continue;
            item.Checked = false;
        }
        if (!streaming || connectedId is null) return;

        foreach (var (item, id) in _airPlayItemIds)
            if (id == connectedId) item.Checked = true;
    }

    /// <summary>Rebuilds the HUD submenu: the arrange switch, one checkable item per widget the
    /// user has, and the add list. Rebuilt on every menu open for the same reason the EQ presets
    /// are — the layout can change from the widgets themselves while the menu is closed.</summary>
    public void SetHudState(bool editMode, IReadOnlyList<HudWidget> widgets)
    {
        // DropDownItems.Clear() only unparents; the discarded items are disposed after the Clear,
        // never before — an item is still owned by the collection until it is removed. Everything
        // except the two items this class owns for the tray's whole life.
        var discarded = _hudMenu.DropDownItems.Cast<ToolStripItem>()
            .Where(i => !ReferenceEquals(i, _hudEditItem) && !ReferenceEquals(i, _hudAddMenu))
            .ToArray();
        _hudMenu.DropDownItems.Clear();
        foreach (var item in discarded) item.Dispose();

        _hudEditItem.Checked = editMode;
        _hudMenu.DropDownItems.Add(_hudEditItem);
        _hudMenu.DropDownItems.Add(_hudAddMenu);
        if (widgets.Count > 0)
        {
            _hudMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var widget in widgets)
            {
                var id = widget.Id;
                _hudMenu.DropDownItems.Add(new ToolStripMenuItem(
                    HudWidgetTypes.DisplayName(widget.Type), null, (_, _) => HudWidgetToggled?.Invoke(id))
                {
                    Checked = widget.Visible,
                });
            }
        }
    }

    public void ShowWarning(string text) => Show(text, ToolTipIcon.Warning, 5000, onClick: null);

    public void ShowInfo(string text) => Show(text, ToolTipIcon.Info, 5000, onClick: null);

    /// <summary>An informational balloon that DOES something when clicked, and stays up longer
    /// because it is asking to be clicked rather than just reporting.</summary>
    public void ShowActionableInfo(string text, Action onClick) =>
        Show(text, ToolTipIcon.Info, 10000, onClick);

    /// <summary>An info balloon that runs <paramref name="onClick"/> when clicked — used by the
    /// updater's "new version available — click to open the release page" notice when the exe
    /// directory isn't writable for the in-place swap.</summary>
    public void ShowNotice(string text, Action onClick) => Show(text, ToolTipIcon.Info, 10000, onClick);

    /// <summary>A warning balloon that runs <paramref name="onClick"/> when clicked, and stays up
    /// longer because it is asking the user to do something — the Equalizer APO health monitor's
    /// "this stopped working, here is where to fix it". A warning nobody can act on from where it
    /// appears is just an interruption.</summary>
    public void ShowActionableWarning(string text, Action onClick) =>
        Show(text, ToolTipIcon.Warning, 10000, onClick);

    /// <summary>The one place a balloon is raised, so the click action is always set (or cleared)
    /// in the same breath: a stale action left behind by an earlier balloon would fire from a
    /// click on this one.</summary>
    private void Show(string text, ToolTipIcon icon, int timeoutMs, Action? onClick)
    {
        _balloonClickAction = onClick;
        _icon.ShowBalloonTip(timeoutMs, "AorinEQ", text, icon);
    }

    /// <summary>Order matters: stop the system events (they'd re-apply an icon we're about to
    /// destroy), then take the icon off the taskbar, and only then free the glyph handles — the
    /// shell must not be holding one when it is destroyed. The context menu is disposed last
    /// because NotifyIcon does not own it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        // Loc.LanguageChanged is a STATIC event: a subscriber that never detaches keeps this whole
        // object, its icon and its menu alive for the life of the process.
        Loc.LanguageChanged -= Retranslate;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _renderer.Dispose();
        _menu.Dispose();
    }
}
