using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AorinEQ.Core;
using AorinEQ.Input;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Border = System.Windows.Controls.Border;
// Aliased because this class inherits FrameworkElement, where HorizontalAlignment and
// VerticalAlignment are PROPERTIES - so the bare enum names resolve to those instead.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;
using Size = System.Windows.Size;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AorinEQ.UI;

/// <summary>The AirPlay strip under the volume bar: which receiver, how loud it is, and the way
/// to change either.
///
/// ONE control for both OSDs. The Fluent OSD and the skinned OSD are different windows with
/// different lifetimes, but the AirPlay bar is the same thing in both, and a second copy is how
/// the two would drift into disagreeing about what a click does.
///
/// It draws itself two ways. Given a <see cref="SkinAirPlay"/> it composes the skin's artwork
/// exactly as <see cref="SkinView"/> composes the volume bar - same frame loader, same fill
/// maths, same text alignment. Given null it draws a native strip instead, which is what a skin
/// written before this existed gets, and what the non-skinned OSD styles always get. Those two
/// paths are the whole point: a legacy skin keeps working and simply gains a plain AirPlay bar
/// under its own artwork.</summary>
public sealed class AirPlayBarView : UserControl
{
    /// <summary>The user clicked the part of the bar that opens the device list.</summary>
    public event Action? DropdownRequested;

    /// <summary>The power button was tapped while there was a chosen receiver and no session.
    /// The receiver is the one in SETTINGS, which the strip may never have discovered - see
    /// AirPlaySetting.Connecting.</summary>
    public event Action? ConnectChosenRequested;

    /// <summary>The power button was tapped while a session was live.</summary>
    public event Action? DisconnectRequested;

    /// <summary>The user set the RECEIVER's level by dragging the bar. Not the system volume -
    /// see AirPlayOwnsVolume in App.AirPlay for why those stay separate.</summary>
    public event Action<int>? VolumeSetByUser;

    /// <summary>A wheel notch over the strip, RAW. It is not turned into a percentage here: a
    /// high-resolution wheel or a precision touchpad sends many small deltas per detent, and
    /// treating each as a whole step overshoots wildly. App feeds them through the same
    /// ScrollStep accumulator the volume bar already uses.</summary>
    public event Action<WheelNotch>? VolumeScrolled;

    private const double NativeHeight = 30;
    private const double NativeCorner = 6;
    private const double GlyphColumn = 26;   // the chevron's width at the right-hand end
    private const double PowerColumn = 26;   // the power button, immediately left of it

    private static readonly Color NativeBack = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);
    private static readonly Color NativeFill = Color.FromArgb(0xFF, 0x4C, 0x8E, 0xFF);
    private static readonly Color NativeDim = Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E);

    /// <summary>How far the power button is faded while it would CONNECT rather than disconnect.
    /// Lit means "audio is going there", which is a thing worth being able to read at a glance
    /// from across a room.</summary>
    private const double PowerDimOpacity = 0.45;

    /// <summary>Segoe MDL2's power glyph, and the same font the chevron beside it already uses.</summary>
    private const string PowerGlyph = "";

    private readonly Grid _root = new();

    // Skinned path.
    private readonly Image _emptyImage = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Image _fullImage = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly RectangleGeometry _fillClip = new();
    private readonly TextBlock _skinName = new();
    private readonly TextBlock _skinPercent = new();

    /// <summary>The power button over a SKIN's artwork. Drawn by the app rather than by the skin
    /// for the same reason the name and the level are: it changes with the session, and a PNG
    /// cannot. The skin says where it goes and what colour it is when lit; this draws it.</summary>
    private readonly TextBlock _skinPower = new();

    // Native path.
    private readonly Border _nativeBack = new() { CornerRadius = new CornerRadius(NativeCorner) };
    private readonly Border _nativeFill = new()
    {
        CornerRadius = new CornerRadius(NativeCorner),
        HorizontalAlignment = HAlign.Left,
    };
    private readonly TextBlock _nativeName = new();
    private readonly TextBlock _nativePercent = new();
    private readonly TextBlock _nativeGlyph = new();
    private readonly TextBlock _nativePower = new();

    private SkinAirPlay? _skin;
    private double _scale = 1.0;
    private AirPlayBarState _state = AirPlayBarState.Hidden;
    private bool _dragging;

    public AirPlayBarView()
    {
        Content = _root;
        Focusable = false;
        BuildNative();
        BuildSkinned();
        ApplyMode();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        LostMouseCapture += (_, _) => _dragging = false;
    }

    /// <summary>The state the bar last rendered. The OSD reads it back rather than tracking a
    /// second copy, so there is one answer to "what is on screen".</summary>
    public AirPlayBarState State => _state;

    /// <summary>Switches between the skinned and native looks. Null means native, which is both
    /// the fallback for a skin with no AirPlay artwork and the only option for the Fluent,
    /// dark-pill and minimal OSD styles.</summary>
    public void SetSkin(SkinAirPlay? skin, double scale)
    {
        _skin = skin;
        _scale = scale <= 0 ? 1.0 : scale;

        if (skin is not null)
        {
            // The same loader the volume bar uses, so a sprite sheet or a GIF behaves here exactly
            // as it does there. Only the first frame is shown: an animated AirPlay strip is not
            // something anybody has asked for, and holding a second animation timer open for a
            // transient OSD would cost more than it is worth.
            _emptyImage.Source = SkinFrames.Load(skin.EmptyPath, skin.EmptyFrames, 10).Frames[0];
            _fullImage.Source = SkinFrames.Load(skin.FullPath, skin.FullFrames, 10).Frames[0];
            Width = skin.Width * _scale;
            Height = skin.Height * _scale;
        }
        else
        {
            Height = NativeHeight;
            Width = double.NaN;   // the host decides the width of a native strip
        }

        ApplyMode();
        Render();
    }

    /// <summary>Shows one reading. A hidden state collapses the control rather than drawing an
    /// empty strip, so the OSD's height is right without the caller doing anything.</summary>
    public void SetState(AirPlayBarState state)
    {
        _state = state;
        Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (state.Visible) Render();
    }

    private void BuildSkinned()
    {
        _fullImage.Clip = _fillClip;
        _root.Children.Add(_emptyImage);
        _root.Children.Add(_fullImage);

        foreach (var t in new[] { _skinName, _skinPercent })
        {
            t.HorizontalAlignment = HAlign.Left;
            t.VerticalAlignment = VAlign.Top;
            t.IsHitTestVisible = false;
            _root.Children.Add(t);
        }

        _skinPower.Text = PowerGlyph;
        _skinPower.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
        _skinPower.HorizontalAlignment = HAlign.Left;
        _skinPower.VerticalAlignment = VAlign.Top;
        _skinPower.TextAlignment = TextAlignment.Center;
        // Not hit-testable: the strip owns its own mouse handling and decides by REGION, so a
        // glyph that swallowed clicks would make the button work only where the ink is.
        _skinPower.IsHitTestVisible = false;
        _root.Children.Add(_skinPower);
    }

    private void BuildNative()
    {
        _nativeBack.Background = new SolidColorBrush(NativeBack);
        _nativeFill.Background = new SolidColorBrush(NativeFill);
        _root.Children.Add(_nativeBack);
        _root.Children.Add(_nativeFill);

        // Explicit foregrounds, like every other text in this app: a TextBlock that inherits
        // nothing renders black, and black on a dark OSD is the contrast defect this project has
        // already shipped once and had to pixel-sample to find.
        _nativeName.Foreground = Brushes.White;
        _nativeName.VerticalAlignment = VAlign.Center;
        _nativeName.Margin = new Thickness(10, 0, 0, 0);
        _nativeName.TextTrimming = TextTrimming.CharacterEllipsis;
        _nativeName.IsHitTestVisible = false;

        _nativePercent.Foreground = new SolidColorBrush(Colors.White);
        _nativePercent.VerticalAlignment = VAlign.Center;
        _nativePercent.HorizontalAlignment = HAlign.Right;
        _nativePercent.Margin = new Thickness(0, 0, GlyphColumn + PowerColumn + 4, 0);
        _nativePercent.IsHitTestVisible = false;

        _nativeGlyph.Text = "";                       // Segoe MDL2 chevron-down
        _nativeGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
        _nativeGlyph.FontSize = 10;
        _nativeGlyph.Foreground = Brushes.White;
        _nativeGlyph.VerticalAlignment = VAlign.Center;
        _nativeGlyph.HorizontalAlignment = HAlign.Right;
        _nativeGlyph.Margin = new Thickness(0, 0, 10, 0);
        _nativeGlyph.IsHitTestVisible = false;

        _nativePower.Text = PowerGlyph;
        _nativePower.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
        _nativePower.FontSize = 12;
        _nativePower.Foreground = Brushes.White;
        _nativePower.VerticalAlignment = VAlign.Center;
        _nativePower.HorizontalAlignment = HAlign.Right;
        _nativePower.Margin = new Thickness(0, 0, GlyphColumn + 4, 0);
        _nativePower.IsHitTestVisible = false;

        _root.Children.Add(_nativeName);
        _root.Children.Add(_nativePercent);
        _root.Children.Add(_nativeGlyph);
        _root.Children.Add(_nativePower);
    }

    private void ApplyMode()
    {
        bool skinned = _skin is not null;
        var skinVis = skinned ? Visibility.Visible : Visibility.Collapsed;
        var nativeVis = skinned ? Visibility.Collapsed : Visibility.Visible;

        _emptyImage.Visibility = skinVis;
        _fullImage.Visibility = skinVis;
        _skinName.Visibility = skinVis;
        _skinPercent.Visibility = skinVis;
        _skinPower.Visibility = skinVis;

        _nativeBack.Visibility = nativeVis;
        _nativeFill.Visibility = nativeVis;
        _nativeName.Visibility = nativeVis;
        _nativePercent.Visibility = nativeVis;
        _nativeGlyph.Visibility = nativeVis;
        _nativePower.Visibility = nativeVis;
    }

    private void Render()
    {
        if (_skin is { } skin) RenderSkinned(skin);
        else RenderNative();
    }

    private void RenderSkinned(SkinAirPlay skin)
    {
        double w = skin.Width * _scale;
        double h = skin.Height * _scale;

        double fill = SkinMath.FillWidth(skin.Width, _state.VolumePercent, skin.FillStartX, skin.FillEndX) * _scale;
        _fillClip.Rect = new Rect(0, 0, fill, h);

        ApplySkinText(_skinName, skin.Name, DisplayName, w);
        ApplySkinText(_skinPercent, skin.Percent, _state.VolumePercent.ToString(), w);
        ApplySkinPower(skin);
    }

    /// <summary>Places and colours the power button over a skin's artwork.
    ///
    /// Hidden entirely when the skin declares no connectHit - a skin written before this existed
    /// must not sprout a control it never drew room for - and when there is no receiver to act on,
    /// because a lit-looking button that does nothing is worse than no button.</summary>
    private void ApplySkinPower(SkinAirPlay skin)
    {
        if (skin.ConnectHit is not { } hit || _state.Power == AirPlayPower.Unavailable)
        {
            _skinPower.Visibility = Visibility.Collapsed;
            return;
        }

        _skinPower.Visibility = Visibility.Visible;

        // Sized to the region the skin reserved, so a big button on a big skin is a big glyph.
        // Two thirds of the shorter side leaves the ring clear of the edges it is centred in.
        double side = Math.Min(hit.Width, hit.Height) * _scale;
        _skinPower.FontSize = Math.Max(8, side * 0.62);
        _skinPower.Width = hit.Width * _scale;
        _skinPower.Height = hit.Height * _scale;

        bool live = _state.Power == AirPlayPower.Disconnect;
        _skinPower.Foreground = live
            ? ParseBrush(skin.ConnectColor ?? skin.Name?.Color)
            : ParseBrush(skin.Name?.Color);
        _skinPower.Opacity = live ? 1.0 : PowerDimOpacity;

        // Centred vertically inside its own box: the glyph's line box is taller than its ink, so
        // top-anchoring it would sit it high in the region the user is aiming at.
        double top = hit.Y * _scale + (hit.Height * _scale - _skinPower.FontSize * 1.35) / 2;
        _skinPower.Margin = new Thickness(hit.X * _scale, Math.Max(0, top), 0, 0);
    }

    private void ApplySkinText(TextBlock block, SkinText? spec, string value, double width)
    {
        if (spec is not { Show: true })
        {
            block.Visibility = Visibility.Collapsed;
            return;
        }

        block.Visibility = Visibility.Visible;
        block.Text = value;
        block.FontFamily = new FontFamily(spec.FontFamily);
        block.FontSize = spec.FontSize * _scale;
        block.FontWeight = spec.Bold ? FontWeights.Bold : FontWeights.Normal;
        block.Foreground = ParseBrush(spec.Color);

        // Zeroed BEFORE measuring. DesiredSize INCLUDES the margin, so measuring with the
        // previous placement still applied feeds the offset back into itself - the right-aligned
        // level walked further left on every render until it fell off the front of the bar.
        block.Margin = new Thickness(0);
        block.Measure(new Size(width, double.PositiveInfinity));
        block.Margin = new Thickness(
            SkinMath.AlignedTextX(spec.X * _scale, block.DesiredSize.Width, spec.Align),
            spec.Y * _scale, 0, 0);
    }

    private void RenderNative()
    {
        _nativeName.Text = DisplayName;
        _nativePercent.Text = _state.HasDevice ? _state.VolumePercent + "%" : "";

        // Dimmed until there is a session. The bar is still usable - that is how you connect - but
        // it should not look like audio is going somewhere it is not.
        _nativeFill.Background = new SolidColorBrush(_state.IsStreaming ? NativeFill : NativeDim);
        _nativeFill.Visibility = _state.HasDevice ? Visibility.Visible : Visibility.Collapsed;

        double usable = Math.Max(0, ActualWidth);
        _nativeFill.Width = usable * Math.Clamp(_state.VolumePercent, 0, 100) / 100.0;
        _nativeFill.Height = NativeHeight;

        // Same rule as the skinned button: shown only when there is something for it to do.
        bool live = _state.Power == AirPlayPower.Disconnect;
        _nativePower.Visibility = _state.Power == AirPlayPower.Unavailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        _nativePower.Foreground = new SolidColorBrush(live ? NativeFill : Colors.White);
        _nativePower.Opacity = live ? 1.0 : PowerDimOpacity;
    }

    /// <summary>What the bar calls the receiver. A chosen device shows its name; nothing chosen
    /// says so, because an empty strip with a chevron gives the user nothing to aim at.</summary>
    private string DisplayName => _state.HasDevice ? _state.DeviceName : Loc.T("osd.airplay.no-device");

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_skin is null && _state.Visible) RenderNative();
    }

    // ---------------------------------------------------------------- input ---

    /// <summary>Whether a point opens the dropdown rather than setting the volume.
    ///
    /// A skin says where that is; the loader guarantees the region is inside the artwork, and a
    /// skin that declares nothing gets the whole bar - so a two-PNG skin is still usable rather
    /// than being a decorative strip with no way into the device list. The native bar reserves
    /// its chevron column at the right-hand end.</summary>
    private bool IsDropdownPoint(Point p)
    {
        if (_skin is { } skin)
            return skin.DropdownHit.Contains(p.X / _scale, p.Y / _scale);

        return p.X >= ActualWidth - GlyphColumn;
    }

    /// <summary>Whether a point is the power button.
    ///
    /// Tested BEFORE the dropdown, because a skin that declares no dropdownHit gets the whole bar
    /// as one, and the connect region has to be able to live inside that. Only where the button is
    /// actually drawn: a region declared by the skin but showing nothing - no receiver chosen -
    /// falls through to the dropdown, which is where a first receiver gets picked.</summary>
    private bool IsPowerPoint(Point p)
    {
        if (_state.Power == AirPlayPower.Unavailable) return false;

        if (_skin is { } skin)
            return skin.ConnectHit is { } hit && hit.Contains(p.X / _scale, p.Y / _scale);

        double right = ActualWidth - GlyphColumn;
        return p.X >= right - PowerColumn && p.X < right;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_state.Visible) return;

        var at = e.GetPosition(this);

        if (IsPowerPoint(at))
        {
            if (_state.Power == AirPlayPower.Disconnect) DisconnectRequested?.Invoke();
            else ConnectChosenRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (IsDropdownPoint(at))
        {
            DropdownRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // No receiver means no level to set, so a drag would be writing a volume to nothing.
        if (!_state.HasDevice) return;

        _dragging = true;
        CaptureMouse();
        SetVolumeFrom(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            SetVolumeFrom(e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_state.Visible || !_state.HasDevice) return;

        VolumeScrolled?.Invoke(new WheelNotch(
            e.Delta,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
        e.Handled = true;
    }

    private void SetVolumeFrom(Point p)
    {
        int percent = _skin is { } skin
            ? SkinMath.PercentFromX(p.X / _scale, skin.FillStartX, skin.FillEndX)
            : SkinMath.PercentFromX(p.X, 0, Math.Max(1, ActualWidth));

        VolumeSetByUser?.Invoke(percent);
    }

    private static Brush ParseBrush(string? color)
    {
        // Null is not a malformed colour, it is an absent one: the power button falls back through
        // connectColor to the name's colour to white, and any of those may simply not be set.
        if (string.IsNullOrWhiteSpace(color)) return Brushes.White;

        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        catch (FormatException)
        {
            // Same posture as the rest of the skin pipeline: a malformed colour is an authoring
            // mistake that degrades to something readable rather than crashing the render path.
            return Brushes.White;
        }
    }
}
