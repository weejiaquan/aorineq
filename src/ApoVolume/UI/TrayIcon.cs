using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using ApoVolume.Core;
using Microsoft.Win32;

namespace ApoVolume.UI;

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
    private readonly TrayIconRenderer _renderer = new();
    // Windows raises UserPreferenceChanged/DisplaySettingsChanged on its own thread, while both
    // NotifyIcon and the renderer are affine to the thread that built them.
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private int _percent;
    private bool _muted;
    private bool _disposed;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _eqPresetMenu;
    private Action? _balloonClickAction;

    public event Action? OpenRequested;
    public event Action? MuteToggleRequested;
    public event Action? SettingsRequested;
    public event Action? EqualizerRequested;
    public event Action? ExitRequested;

    /// <summary>An EQ preset was picked from the tray submenu, by name.</summary>
    public event Action<string>? EqPresetSelected;

    /// <summary>Raised right before the context menu opens — the app refreshes the EQ preset
    /// submenu here so it always shows the current preset files and active selection.</summary>
    public event Action? MenuOpening;

    public TrayIcon()
    {
        _muteItem = new ToolStripMenuItem("Mute", null, (_, _) => MuteToggleRequested?.Invoke());
        _eqPresetMenu = new ToolStripMenuItem("EQ preset");

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open volume slider", null, (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripMenuItem("Open equalizer…", null, (_, _) => EqualizerRequested?.Invoke()));
        menu.Items.Add(_eqPresetMenu);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));
        menu.Opening += (_, _) => MenuOpening?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = CurrentIcon(),
            Text = "ApoVolume",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
        // One handler for the lifetime of the icon; each balloon sets (or clears) the action so
        // a click on a stale balloon can never fire a newer balloon's action.
        _icon.BalloonTipClicked += (_, _) => _balloonClickAction?.Invoke();
        _icon.BalloonTipClosed += (_, _) => _balloonClickAction = null;

        // The glyph is theme- and size-dependent, and both can change while the app sits idle:
        // switching Windows to dark mode, or moving the taskbar to a monitor at another DPI. Both
        // are re-read by CurrentIcon, so both handlers just re-apply.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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
        _icon.Text = muted ? "ApoVolume: muted" : $"ApoVolume: {percent}%";
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
            _dispatcher.BeginInvoke(new Action(RefreshIcon));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(new Action(RefreshIcon));

    /// <summary>Re-applies the glyph after a theme or DPI change. Guarded because a callback can
    /// already be queued on the dispatcher when the tray is disposed at shutdown.</summary>
    private void RefreshIcon()
    {
        if (_disposed) return;
        _icon.Icon = CurrentIcon();
    }

    /// <summary>Rebuilds the "EQ preset" submenu for the active device's scope: one checkable
    /// item per preset file, the active one checked. Empty list disables the submenu.</summary>
    public void SetEqPresets(IReadOnlyList<string> names, string activeName)
    {
        _eqPresetMenu.DropDownItems.Clear();
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

    public void ShowWarning(string text)
    {
        _balloonClickAction = null;
        _icon.ShowBalloonTip(5000, "ApoVolume", text, ToolTipIcon.Warning);
    }

    public void ShowInfo(string text)
    {
        _balloonClickAction = null;
        _icon.ShowBalloonTip(5000, "ApoVolume", text, ToolTipIcon.Info);
    }

    /// <summary>An info balloon that runs <paramref name="onClick"/> when clicked — used by the
    /// updater's "new version available — click to open the release page" notice when the exe
    /// directory isn't writable for the in-place swap.</summary>
    public void ShowNotice(string text, Action onClick)
    {
        _balloonClickAction = onClick;
        _icon.ShowBalloonTip(10000, "ApoVolume", text, ToolTipIcon.Info);
    }

    /// <summary>Order matters: stop the system events (they'd re-apply an icon we're about to
    /// destroy), then take the icon off the taskbar, and only then free the glyph handles — the
    /// shell must not be holding one when it is destroyed.</summary>
    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _renderer.Dispose();
    }
}
