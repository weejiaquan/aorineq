using System.Drawing;
using System.Windows.Forms;

namespace ApoVolume.UI;

/// <summary>WinForms NotifyIcon wrapper: app icon, state tooltip, context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
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
        _appIcon = LoadAppIcon();

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
            Icon = _appIcon,
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
    }

    /// <summary>The icon itself is the app brand and never changes; mute state reads from the
    /// tooltip and the checked "Mute" menu item.</summary>
    public void Update(int percent, bool muted)
    {
        _icon.Text = muted ? "ApoVolume: muted" : $"ApoVolume: {percent}%";
        _muteItem.Checked = muted;
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

    /// <summary>Loads the shipped multi-size app icon, asking for the shell's current small-icon
    /// size so Windows picks the matching frame instead of downscaling the 256px one.</summary>
    private static Icon LoadAppIcon()
    {
        using var stream = System.Windows.Application
            .GetResourceStream(new Uri("pack://application:,,,/ApoVolume.ico"))!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
