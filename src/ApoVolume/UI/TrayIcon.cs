using System.Drawing;
using System.Windows.Forms;

namespace ApoVolume.UI;

/// <summary>WinForms NotifyIcon wrapper: state icon, tooltip, context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _normalIcon;
    private readonly Icon _mutedIcon;
    private readonly ToolStripMenuItem _muteItem;

    public event Action? OpenRequested;
    public event Action? MuteToggleRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _normalIcon = CreateGlyphIcon("\uE767");
        _mutedIcon = CreateGlyphIcon("\uE74F");

        _muteItem = new ToolStripMenuItem("Mute", null, (_, _) => MuteToggleRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open volume slider", null, (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()));
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

    public void ShowWarning(string text) =>
        _icon.ShowBalloonTip(5000, "apo-volume", text, ToolTipIcon.Warning);

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
