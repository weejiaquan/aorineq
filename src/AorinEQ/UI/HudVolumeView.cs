using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AorinEQ.Core;

// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Brushes = System.Windows.Media.Brushes;

namespace AorinEQ.UI;

/// <summary>The volume widget. THE ONE WIDGET THAT RENDERS THROUGH THE SKIN PIPELINE, and the
/// reason <see cref="SkinView"/> exists as a control rather than as the body of a window.
///
/// It draws no bar of its own. It hosts the very same view the OSD hosts, fed from the very same
/// <see cref="SkinLoader"/> output, so everything skins can already do — animation, fill ranges,
/// styled percent text, dedicated muted artwork — works here on the day this ships, without a line
/// of code that knows about any of it. Skin format v2 then upgrades the HUD for free.
///
/// When no skin is available (the user is on a built-in OSD style, or their skin failed to load)
/// the widget falls back to a plain readout rather than to a hand-drawn imitation of a skin. A
/// bespoke bar here would be the second renderer this design exists to avoid, and it would be the
/// one that quietly stops matching.</summary>
internal sealed class HudVolumeView : Grid, IHudWidgetView
{
    private readonly Viewbox _skinBox = new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.Both,
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _fallback = new()
    {
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
    };
    private readonly TextBlock _percentText = new()
    {
        FontSize = 26,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
    };
    private readonly TextBlock _dbText = new()
    {
        FontSize = 11,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
    };
    private readonly TextBlock _deviceText = new()
    {
        FontSize = 11,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Visibility = Visibility.Collapsed,
    };

    private SkinView? _skin;
    private string? _skinFolder;
    private HudWidget _widget = HudWidget.Create(HudWidgetTypes.Volume);
    private EqPalette _palette = EqPalette.Dark;
    private int _lastPercent = -1;
    private bool _lastMuted;
    private double? _lastDb;
    private string _lastDevice = "";

    public HudVolumeView()
    {
        _fallback.Children.Add(_percentText);
        _fallback.Children.Add(_dbText);
        _fallback.Children.Add(_deviceText);
        Children.Add(_skinBox);
        Children.Add(_fallback);
    }

    /// <summary>Installs (or removes) the skin this widget renders. Called by the HUD whenever the
    /// active skin changes, with exactly the <see cref="SkinInfo"/> the OSD is using — one loader,
    /// one validation, one set of pixels.</summary>
    public void SetSkin(SkinInfo? info)
    {
        if (info is null || !info.IsValid)
        {
            Detach();
            return;
        }
        if (_skin is not null && _skinFolder == info.Folder) return;

        Detach();
        try
        {
            // SkinLoader only validates the PNG header, so a truncated file still passes IsValid
            // and throws from the decoder here. Contained exactly as App.ApplyOsdConfig contains
            // it: the widget falls back to the readout instead of taking the process down.
            _skin = new SkinView(info) { Zoom = _widget.Scale };
            _skinFolder = info.Folder;
            _skinBox.Child = _skin;
            _skinBox.Visibility = Visibility.Visible;
            _fallback.Visibility = Visibility.Collapsed;
            _lastPercent = -1; // force a compose on the next frame
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException
            or System.IO.IOException or ArgumentException or OutOfMemoryException)
        {
            Detach();
        }
    }

    private void Detach()
    {
        _skin?.StopAnimations();
        _skinBox.Child = null;
        _skin = null;
        _skinFolder = null;
        _skinBox.Visibility = Visibility.Collapsed;
        _fallback.Visibility = Visibility.Visible;
        _lastPercent = -1;
    }

    public void Apply(HudWidget widget)
    {
        _widget = widget;
        if (_skin is not null) _skin.Zoom = widget.Scale;
        _deviceText.Visibility = widget.ShowDeviceName ? Visibility.Visible : Visibility.Collapsed;
        _lastPercent = -1;
    }

    public void ApplyPalette(EqPalette palette)
    {
        _palette = palette;
        var text = new SolidColorBrush(HudWidgetWindow.ToMediaColor(palette.Text));
        var dim = new SolidColorBrush(HudWidgetWindow.ToMediaColor(palette.TextDim));
        text.Freeze();
        dim.Freeze();
        _percentText.Foreground = text;
        _dbText.Foreground = dim;
        _deviceText.Foreground = dim;
    }

    /// <summary>Only when the volume state actually changed. The volume is not a signal — it sits
    /// still for minutes at a time, and a widget that recomposed a skin 30 times a second to show
    /// the same number would be the per-widget cost this design set out to avoid.</summary>
    public bool NeedsRedraw(HudFrame frame) =>
        frame.Percent != _lastPercent
        || frame.Muted != _lastMuted
        || frame.VolumeDb != _lastDb
        || (_widget.ShowDeviceName && frame.DeviceName != _lastDevice);

    public void Render(HudFrame frame)
    {
        _lastPercent = frame.Percent;
        _lastMuted = frame.Muted;
        _lastDb = frame.VolumeDb;
        _lastDevice = frame.DeviceName;

        // The skin composes itself, exactly as it does for the OSD.
        _skin?.SetVolume(frame.Percent, frame.Muted);

        _percentText.Text = frame.Muted
            ? "muted"
            : frame.Percent.ToString(CultureInfo.CurrentCulture) + "%";
        _dbText.Text = frame.VolumeDb is { } db
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", db)
            : "";
        _dbText.Visibility = frame.VolumeDb is null ? Visibility.Collapsed : Visibility.Visible;
        _deviceText.Text = frame.DeviceName;
        _deviceText.ToolTip = frame.DeviceName;
    }
}
