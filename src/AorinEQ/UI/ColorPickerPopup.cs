using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using AorinEQ.Core;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace AorinEQ.UI;

/// <summary>The colour picker, in a popup anchored to whatever asked for it: a
/// saturation/value square, hue and opacity sliders, a hex box, the twelve recent colours and -
/// where the host can sample its canvas - an eyedropper. One picker for the Skin Designer and the
/// HUD widget menu; it replaces the WinForms colour dialog, which had no alpha and opened a window.
///
/// It edits HSV and reports #AARRGGBB on every change (<see cref="ColorChanged"/>), so a host
/// previews live; a host that records history treats one opening as one step. Closing after a
/// change puts the colour at the front of the recents and raises <see cref="RecentsChanged"/>.</summary>
internal sealed class ColorPickerPopup : Popup
{
    internal const double SquareSize = 180;
    private const double ThumbSize = 12;

    private readonly Border _square = new()
    {
        Width = SquareSize, Height = SquareSize, ClipToBounds = true, Cursor = Cursors.Cross,
    };
    private readonly Ellipse _thumb = new()
    {
        Width = ThumbSize, Height = ThumbSize, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false,
    };
    private readonly Border _alphaRamp = new() { Height = 10, CornerRadius = new CornerRadius(2) };

    private double _hue, _saturation, _value;
    private byte _alpha = 0xFF;
    private string _opened = "#FFFFFFFF";
    private bool _syncing;
    private bool _dragging;

    internal Slider HueSlider { get; } = new() { Minimum = 0, Maximum = 360 };
    internal Slider AlphaSlider { get; } = new() { Minimum = 0, Maximum = 255 };
    internal TextBox HexBox { get; } = new() { Width = 120 };
    internal WrapPanel RecentPanel { get; } = new() { Width = SquareSize };
    internal Button EyedropperButton { get; }

    public ColorPickerPopup()
    {
        StaysOpen = false;
        AllowsTransparency = true;
        Placement = PlacementMode.Bottom;

        var surface = new Grid();
        // Fully qualified: inside this class "Color" is the string property, which member lookup
        // finds before the using alias.
        surface.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0) });
        surface.Children.Add(new Rectangle { Fill = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var thumbLayer = new Canvas { IsHitTestVisible = false };
        thumbLayer.Children.Add(_thumb);
        surface.Children.Add(thumbLayer);
        _square.Child = surface;
        _square.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = _square.CaptureMouse();
            SetFromSquare(e.GetPosition(_square));
            e.Handled = true;
        };
        _square.MouseMove += (_, e) =>
        {
            if (_dragging && e.LeftButton == MouseButtonState.Pressed) SetFromSquare(e.GetPosition(_square));
        };
        _square.MouseLeftButtonUp += (_, _) => _square.ReleaseMouseCapture();
        _square.LostMouseCapture += (_, _) => _dragging = false;

        HueSlider.ToolTip = Loc.T("color-picker.hue");
        HueSlider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _hue = HueSlider.Value;
            Sync();
            Raise();
        };
        AlphaSlider.ToolTip = Loc.T("color-picker.opacity");
        AlphaSlider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _alpha = (byte)Math.Round(AlphaSlider.Value);
            Sync();
            Raise();
        };

        HexBox.ToolTip = Loc.T("color-picker.hex.tooltip");
        HexBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitHex();
            e.Handled = true;
        };
        HexBox.LostKeyboardFocus += (_, _) => CommitHex();

        EyedropperButton = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Eyedropper24 },
            ToolTip = Loc.T("color-picker.eyedropper.tooltip"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        EyedropperButton.Click += (_, _) => EyedropperRequested?.Invoke();

        var hueTrack = new Border { Height = 10, CornerRadius = new CornerRadius(2), Background = HueRamp() };
        var alphaTrack = new Grid();
        alphaTrack.Children.Add(new Border { Height = 10, CornerRadius = new CornerRadius(2), Background = DesignerBrushes.Checkerboard });
        alphaTrack.Children.Add(_alphaRamp);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hexRow.Children.Add(HexBox);
        hexRow.Children.Add(EyedropperButton);

        var body = new StackPanel { Width = SquareSize };
        body.Children.Add(_square);
        body.Children.Add(Track(hueTrack, HueSlider));
        body.Children.Add(Track(alphaTrack, AlphaSlider));
        body.Children.Add(hexRow);
        body.Children.Add(new TextBlock { Text = Loc.T("color-picker.recent"), FontSize = 11, Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(RecentPanel);

        var frame = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = body,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Child = frame;
    }

    /// <summary>The colour being edited, #AARRGGBB.</summary>
    public string Color => SkinColor.FromHsv(_hue, _saturation, _value, _alpha).ToHex();

    public IReadOnlyList<string> Recents { get; private set; } = [];

    /// <summary>Whether the host can sample a canvas. The HUD cannot, so it hides the button.</summary>
    public bool EyedropperAvailable
    {
        get => EyedropperButton.Visibility == Visibility.Visible;
        set => EyedropperButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action<string>? ColorChanged;

    public event Action? EyedropperRequested;

    public event Action<IReadOnlyList<string>>? RecentsChanged;

    /// <summary>Opens under <paramref name="anchor"/> on <paramref name="initial"/> (white when it
    /// is not a colour). Opening while already open - another field asking - first remembers the
    /// colour the last opening left: closing and reopening in one turn cancels the close, so
    /// <see cref="OnClosed"/> would never run for it. It goes on top of <paramref name="recents"/>,
    /// which the host read before that colour was pushed.</summary>
    public void Open(UIElement anchor, string initial, IReadOnlyList<string> recents)
    {
        Recents = SkinColor.NormalizeRecents(recents);
        if (IsOpen) Remember();
        IsOpen = false;
        BuildRecents();
        _opened = SkinColor.Format(initial) ?? "#FFFFFFFF";
        Load(SkinColor.Parse(_opened)!.Value);
        PlacementTarget = anchor;
        StaysOpen = false;
        IsOpen = true;
    }

    /// <summary>Takes a colour from outside the picker - a recent swatch, the eyedropper - as if
    /// the user had picked it here.</summary>
    public void SetColor(string color)
    {
        if (SkinColor.Parse(color) is not { } parsed) return;
        Load(parsed);
        Raise();
    }

    internal void SetFromSquare(Point point)
    {
        (_saturation, _value) = SquareToSv(point, SquareSize);
        Sync();
        Raise();
    }

    internal static (double Saturation, double Value) SquareToSv(Point point, double size) =>
        (Unit(point.X / size), Unit(1 - point.Y / size));

    internal static Point SvToSquare(double saturation, double value, double size) =>
        new(saturation * size, (1 - value) * size);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A HUD widget is WS_EX_NOACTIVATE and is never the active window, so keystrokes would keep
        // going to whatever app is in front and the hex box could not be typed into. Bringing the
        // popup's own window forward is what a context menu over such a window does as well.
        if (PlacementTarget is Window { IsActive: false } && PresentationSource.FromVisual(Child) is HwndSource source)
            SetForegroundWindow(source.Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StaysOpen = false;
        Remember();
    }

    /// <summary>Puts the colour at the front of the recents when this opening changed it.</summary>
    private void Remember()
    {
        if (string.Equals(Color, _opened, StringComparison.OrdinalIgnoreCase)) return;
        _opened = Color;
        Recents = SkinColor.PushRecent(Recents, Color);
        BuildRecents();
        RecentsChanged?.Invoke(Recents);
    }

    /// <summary>Clamped to 0-1, and 0 for NaN: Math.Clamp passes NaN through and FromHsv would
    /// turn it into an arbitrary byte.</summary>
    private static double Unit(double fraction) => double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);

    private void Load(SkinColor color)
    {
        var (hue, saturation, value) = color.ToHsv();
        if (saturation > 0 && value > 0) _hue = hue; // a grey has no hue: keep the one the user had
        _saturation = saturation;
        _value = value;
        _alpha = color.A;
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            _square.Background = new SolidColorBrush(PercentTextRenderer.ToMedia(SkinColor.FromHsv(_hue, 1, 1, 0xFF)));
            var opaque = PercentTextRenderer.ToMedia(SkinColor.FromHsv(_hue, _saturation, _value, 0xFF));
            _alphaRamp.Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0);
            var at = SvToSquare(_saturation, _value, SquareSize);
            Canvas.SetLeft(_thumb, at.X - ThumbSize / 2);
            Canvas.SetTop(_thumb, at.Y - ThumbSize / 2);
            HueSlider.Value = _hue;
            AlphaSlider.Value = _alpha;
            HexBox.Text = Color;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Raise() => ColorChanged?.Invoke(Color);

    private void CommitHex()
    {
        if (SkinColor.Parse(HexBox.Text) is { } typed && typed.ToHex() != Color)
        {
            Load(typed);
            Raise();
        }
        else
        {
            HexBox.Text = Color; // formatted, or reverted when it was not a colour
        }
    }

    private void BuildRecents()
    {
        RecentPanel.Children.Clear();
        foreach (var hex in Recents)
        {
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = hex,
                Content = new Border { Background = DesignerBrushes.Checkerboard, Child = new Border { Background = PercentTextRenderer.Brush(hex) } },
            };
            swatch.Click += (_, _) => SetColor(hex);
            RecentPanel.Children.Add(swatch);
        }
    }

    private static Grid Track(FrameworkElement ramp, Slider slider)
    {
        ramp.VerticalAlignment = VerticalAlignment.Center;
        var track = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        track.Children.Add(ramp);
        track.Children.Add(slider);
        return track;
    }

    private static LinearGradientBrush HueRamp()
    {
        var ramp = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i <= 6; i++)
            ramp.GradientStops.Add(new GradientStop(PercentTextRenderer.ToMedia(SkinColor.FromHsv(i * 60, 1, 1, 0xFF)), i / 6.0));
        ramp.Freeze();
        return ramp;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
