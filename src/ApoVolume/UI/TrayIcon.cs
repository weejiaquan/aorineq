using System.Drawing;
using System.Windows.Forms;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>WinForms NotifyIcon wrapper: state icon, tooltip, context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _normalIcon;
    private readonly Icon _mutedIcon;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _autostartItem;

    public event Action? OpenRequested;
    public event Action? MuteToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon(Autostart autostart, string exePath)
    {
        _normalIcon = CreateGlyphIcon("\uE767");
        _mutedIcon = CreateGlyphIcon("\uE74F");

        _muteItem = new ToolStripMenuItem("Mute", null, (_, _) => MuteToggleRequested?.Invoke());
        _autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = autostart.IsEnabled(),
            CheckOnClick = true,
        };
        _autostartItem.CheckedChanged += (_, _) =>
        {
            if (_autostartItem.Checked) autostart.Enable(exePath);
            else autostart.Disable();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open volume slider", null, (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(_muteItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = _normalIcon,
            Text = "apo-volume",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public void Update(int percent, bool muted)
    {
        _icon.Icon = muted ? _mutedIcon : _normalIcon;
        _icon.Text = muted ? "apo-volume: muted" : $"apo-volume: {percent}%";
        _muteItem.Checked = muted;
    }

    private static Icon CreateGlyphIcon(string glyph)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe MDL2 Assets", 22, GraphicsUnit.Pixel);
            g.DrawString(glyph, font, Brushes.White, 1, 3);
        }
        return Icon.FromHandle(bmp.GetHicon()); // two long-lived icons for app lifetime; handles freed on process exit
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normalIcon.Dispose();
        _mutedIcon.Dispose();
    }
}
