using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AorinEQ.Core;

using Color = System.Windows.Media.Color;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using UserControl = System.Windows.Controls.UserControl;
using Image = System.Windows.Controls.Image;

namespace AorinEQ.UI;

/// <summary>A skin, composed and drawn. Everything that makes a skin LOOK like a skin lives here
/// and nowhere else: the two (or three) layers, their animation timers, the fill clip, the
/// complement clip that stops the empty layer stacking under a translucent full one, the percent
/// number, the mute badge, and the per-pixel hit shape.
///
/// IT IS A CONTROL RATHER THAN A WINDOW ON PURPOSE, and that is the one architectural seam this
/// release is built around. Until v3.5.0 this code was the body of <see cref="SkinOsdWindow"/>,
/// so a skin could only ever be a transient OSD. The HUD's volume widget is the second surface
/// that has to render one — and it renders THIS, not a bespoke bar of its own. The Skin Designer
/// is the third: it re-targets one view at every edit through <see cref="Update"/>, which decodes
/// a layer again only when its art actually changed.
///
/// What is deliberately NOT here: where the skin appears, when it appears, whether it fades, and
/// what a click MEANS. Those differ between a transient OSD and a persistent widget, and they
/// belong to the window that owns the view. This class only answers "is this pixel mine" and
/// "what percent is this x" — the two questions both owners have to ask.</summary>
public sealed class SkinView : UserControl
{
    /// <summary>The mute badge's glyph, Segoe Fluent Icons / MDL2 "Mute" (U+E74F).</summary>
    private static readonly string MuteGlyph = ((char)0xE74F).ToString();

    /// <summary>What a decoded layer was decoded FROM. The file's write time is part of it, so art
    /// re-exported under the same name is decoded again; a GIF's fps is not, because its timing
    /// comes from the file.</summary>
    private readonly record struct LayerKey(string Path, int Frames, double Fps, DateTime Written)
    {
        public static LayerKey Of(string path, int frames, double fps) =>
            new(path, frames, ImageProbe.IsGif(path) ? 0 : fps, System.IO.File.GetLastWriteTimeUtc(path));
    }

    private SkinInfo _info;

    // Hit shape = the union of the opaque pixels across ALL frames of the layers CURRENTLY
    // conveying the skin, so an element that is transparent in one animation frame stays clickable
    // throughout. Normal display hit-tests empty∪full; while dedicated muted artwork replaces those
    // layers only ITS pixels are hittable — the hidden bar must not swallow clicks that should fall
    // through to whatever is beneath.
    private AlphaMap _hitMap;
    private AlphaMap? _mutedHitMap;
    private bool _mutedLayerShowing;

    private SkinFrames _emptyFrames;
    private SkinFrames _fullFrames;
    private SkinFrames? _mutedFrames;
    private LayerKey _emptyKey;
    private LayerKey _fullKey;
    private LayerKey? _mutedKey;

    private readonly Image _emptyImage = new() { Stretch = Stretch.Fill };
    private readonly Image _fullImage = new() { Stretch = Stretch.Fill };
    private readonly Image _mutedImage = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly RectangleGeometry _fillClip = new();
    private readonly Path _percentPath = new()
    {
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        VerticalAlignment = System.Windows.VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _muteBadge;

    private readonly DispatcherTimer _emptyAnimTimer = new();
    private readonly DispatcherTimer _fullAnimTimer = new();
    private readonly DispatcherTimer _mutedAnimTimer = new();
    private int _emptyFrameIndex, _fullFrameIndex, _mutedFrameIndex;

    private double _zoom = 1.0;
    private int _lastPercent;
    private bool _lastMuted;

    /// <summary>The skin this view currently draws.</summary>
    public SkinInfo Info => _info;

    internal SkinFrames EmptyFrames => _emptyFrames;
    internal SkinFrames FullFrames => _fullFrames;
    internal SkinFrames? MutedFrames => _mutedFrames;
    internal Path PercentPath => _percentPath;

    /// <summary>Total scale applied to the skin's logical pixels: the skin's own scale times the
    /// owner's zoom. All geometry — size, fill width, text position, hit testing — goes through
    /// this one number, so a widget the user has enlarged still hit-tests where it looks.</summary>
    public double RenderScale => _info.Scale * _zoom;

    public double LogicalWidth => _info.Width * RenderScale;

    public double LogicalHeight => _info.Height * RenderScale;

    /// <summary>Extra zoom on top of the skin's own scale. 1.0 is the OSD's behaviour, unchanged.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            double next = Math.Clamp(value, HudWidget.MinScale, HudWidget.MaxScale);
            if (Math.Abs(next - _zoom) < 1e-9) return;
            _zoom = next;
            ApplySize();
            SetVolume(_lastPercent, _lastMuted); // geometry depends on scale: recompose at once
        }
    }

    /// <summary>Decode failures throw what <see cref="SkinFrames.IsImagingFailure"/> recognises —
    /// the same contract <see cref="SkinFrames.Load"/> has.</summary>
    public SkinView(SkinInfo info)
    {
        RequireValid(info);
        _info = info;
        _emptyKey = LayerKey.Of(info.EmptyPath, info.EmptyFrames, info.Fps);
        _fullKey = LayerKey.Of(info.FullPath, info.FullFrames, info.Fps);
        _mutedKey = info.MutedPath is null ? null : LayerKey.Of(info.MutedPath, info.MutedFrames, info.Fps);
        _emptyFrames = SkinFrames.Load(info.EmptyPath, info.EmptyFrames, info.Fps);
        _fullFrames = SkinFrames.Load(info.FullPath, info.FullFrames, info.Fps);
        _mutedFrames = info.MutedPath is null ? null : SkinFrames.Load(info.MutedPath, info.MutedFrames, info.Fps);
        _hitMap = AlphaMap.Union(_emptyFrames.Frames.Concat(_fullFrames.Frames));
        _mutedHitMap = _mutedFrames is null ? null : AlphaMap.Union(_mutedFrames.Frames);

        // EdgeMode=Aliased on both layers: their clips are complementary, and anti-aliased clip
        // edges each cover the shared boundary column at ~50%, so the desktop bleeds through as a
        // dark seam at the fill edge (the v1.7.1 hotfix). Aliased edges rasterize both clips to the
        // same pixel cutoff, so the layers tile exactly.
        RenderOptions.SetEdgeMode(_emptyImage, EdgeMode.Aliased);
        RenderOptions.SetEdgeMode(_fullImage, EdgeMode.Aliased);
        _fullImage.Clip = _fillClip;
        ShowFirstFrames();

        _muteBadge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x20)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                // As a numeric literal rather than the character itself: U+E74F is invisible in
                // an editor, and a bulk edit (or a heredoc) that mangles it ships a blank badge.
                Text = MuteGlyph,
            },
        };

        var grid = new Grid();
        grid.Children.Add(_emptyImage);
        grid.Children.Add(_fullImage);
        grid.Children.Add(_mutedImage);
        grid.Children.Add(_percentPath);
        grid.Children.Add(_muteBadge);
        Content = grid;
        Background = System.Windows.Media.Brushes.Transparent;

        // Wired once: each tick reads whichever frames the view holds NOW, so Update can swap them.
        _emptyAnimTimer.Tick += (_, _) => Advance(_emptyFrames, ref _emptyFrameIndex, _emptyImage, _emptyAnimTimer);
        _fullAnimTimer.Tick += (_, _) => Advance(_fullFrames, ref _fullFrameIndex, _fullImage, _fullAnimTimer);
        _mutedAnimTimer.Tick += (_, _) =>
        {
            if (_mutedFrames is not null) Advance(_mutedFrames, ref _mutedFrameIndex, _mutedImage, _mutedAnimTimer);
        };

        ApplyText();
        ApplySize();

        // Animated layers cost nothing while nobody can see them.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) StopAnimations();
            else ResumeAnimations();
        };
    }

    /// <summary>Re-targets this view at <paramref name="info"/>. A layer is decoded again only when
    /// its path, frame count, fps (sheets) or file timestamp changed; text, fill range, scale and
    /// mute dim are re-applied without touching the art. Everything that has to be decoded is
    /// decoded BEFORE anything is replaced, so a decode that throws leaves the view drawing exactly
    /// what it drew.</summary>
    public void Update(SkinInfo info)
    {
        RequireValid(info);
        var emptyKey = LayerKey.Of(info.EmptyPath, info.EmptyFrames, info.Fps);
        var fullKey = LayerKey.Of(info.FullPath, info.FullFrames, info.Fps);
        LayerKey? mutedKey = info.MutedPath is null ? null : LayerKey.Of(info.MutedPath, info.MutedFrames, info.Fps);

        bool emptyChanged = emptyKey != _emptyKey;
        bool fullChanged = fullKey != _fullKey;
        bool mutedChanged = mutedKey != _mutedKey;
        var empty = emptyChanged ? SkinFrames.Load(info.EmptyPath, info.EmptyFrames, info.Fps) : _emptyFrames;
        var full = fullChanged ? SkinFrames.Load(info.FullPath, info.FullFrames, info.Fps) : _fullFrames;
        var muted = !mutedChanged ? _mutedFrames
            : info.MutedPath is null ? null : SkinFrames.Load(info.MutedPath, info.MutedFrames, info.Fps);

        _info = info;
        if (emptyChanged || fullChanged || mutedChanged)
        {
            StopAnimations();
            if (emptyChanged || fullChanged) _hitMap = AlphaMap.Union(empty.Frames.Concat(full.Frames));
            if (mutedChanged) _mutedHitMap = muted is null ? null : AlphaMap.Union(muted.Frames);
            (_emptyFrames, _fullFrames, _mutedFrames) = (empty, full, muted);
            (_emptyKey, _fullKey, _mutedKey) = (emptyKey, fullKey, mutedKey);
            _emptyFrameIndex = _fullFrameIndex = _mutedFrameIndex = 0;
            ShowFirstFrames();
        }
        ApplyText();
        ApplySize();
        SetVolume(_lastPercent, _lastMuted);
    }

    private static void RequireValid(SkinInfo info)
    {
        if (!info.IsValid)
            throw new ArgumentException($"Cannot render an invalid skin: {info.Error}", nameof(info));
    }

    private void ShowFirstFrames()
    {
        _emptyImage.Source = _emptyFrames.Frames[0];
        _fullImage.Source = _fullFrames.Frames[0];
        _mutedImage.Source = _mutedFrames?.Frames[0];
    }

    private void ApplyText() =>
        _percentPath.Visibility = _info.Text is { Show: true } ? Visibility.Visible : Visibility.Collapsed;

    private static void Advance(SkinFrames frames, ref int index, Image image, DispatcherTimer timer)
    {
        if (!frames.IsAnimated)
        {
            timer.Stop();
            return;
        }
        index = (index + 1) % frames.Frames.Count;
        image.Source = frames.Frames[index];
        timer.Interval = frames.Delays[index];
    }

    private void ApplySize()
    {
        Width = LogicalWidth;
        Height = LogicalHeight;
    }

    /// <summary>Composes the skin for one volume state. The whole of the skin's appearance is
    /// decided here — every caller, transient OSD or persistent widget, gets exactly this.</summary>
    public void SetVolume(int percent, bool muted)
    {
        _lastPercent = percent;
        _lastMuted = muted;
        double scale = RenderScale;
        double w = LogicalWidth, h = LogicalHeight;

        if (_info.Text is { Show: true } text)
        {
            double textWidth = PercentTextRenderer.Update(_percentPath, text, percent.ToString(),
                scale, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // X is the anchor (left edge / centre / right edge per align); recomputed on every
            // update since the measured width changes with the digit count.
            _percentPath.Margin = new Thickness(
                SkinMath.AlignedTextX(text.X * scale, textWidth, text.Align), text.Y * scale, 0, 0);
        }

        double fillWidth = SkinMath.FillWidth(_info.Width, percent, _info.FillStartX, _info.FillEndX) * scale;
        _fillClip.Rect = new Rect(0, 0, fillWidth, h);
        // Empty shows everywhere except the filled bar region, so it never stacks under the
        // (possibly translucent) full layer there, while decoration outside the fill range keeps
        // showing. Muted: full is hidden, so empty covers the whole canvas.
        _emptyImage.Clip = muted
            ? null
            : SkinComposite.ComplementClip(_info.FillStartX * scale, fillWidth, w, h);

        // Muted with dedicated artwork: the muted layer alone conveys mute (no badge, no dim).
        // Muted without one: the classic dimmed empty + badge, dimmed by the skin's mutedDim.
        bool useMutedLayer = muted && _mutedFrames is not null;
        _mutedLayerShowing = useMutedLayer; // hit-testing follows what is actually displayed
        _mutedImage.Visibility = useMutedLayer ? Visibility.Visible : Visibility.Collapsed;
        _emptyImage.Visibility = useMutedLayer ? Visibility.Hidden : Visibility.Visible;
        _fullImage.Visibility = muted ? Visibility.Hidden : Visibility.Visible;
        _emptyImage.Opacity = muted && !useMutedLayer ? _info.MutedDim : 1.0;
        _muteBadge.Visibility = muted && !useMutedLayer ? Visibility.Visible : Visibility.Collapsed;

        if (IsVisible) ResumeAnimations();
    }

    /// <summary>Starts the animation timers for whichever layers are actually on screen.</summary>
    public void ResumeAnimations()
    {
        if (_emptyFrames.IsAnimated && !_emptyAnimTimer.IsEnabled)
        {
            _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
            _emptyAnimTimer.Start();
        }
        if (_fullFrames.IsAnimated && !_fullAnimTimer.IsEnabled)
        {
            _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
            _fullAnimTimer.Start();
        }
        if (_mutedLayerShowing && _mutedFrames is { IsAnimated: true } && !_mutedAnimTimer.IsEnabled)
        {
            _mutedAnimTimer.Interval = _mutedFrames.Delays[_mutedFrameIndex];
            _mutedAnimTimer.Start();
        }
        else if (!_mutedLayerShowing)
        {
            _mutedAnimTimer.Stop(); // don't animate a collapsed layer
        }
    }

    /// <summary>Stops every animation timer. Called when the owner hides, and at teardown — a
    /// DispatcherTimer left running roots this view (and every decoded frame behind it) for the
    /// life of the process.</summary>
    public void StopAnimations()
    {
        _emptyAnimTimer.Stop();
        _fullAnimTimer.Stop();
        _mutedAnimTimer.Stop();
    }

    /// <summary>Whether a point in THIS control's coordinates lands on artwork rather than on a
    /// transparent pixel.</summary>
    public bool IsOpaqueAt(System.Windows.Point point)
    {
        double scale = RenderScale;
        int px = (int)(point.X / scale);
        int py = (int)(point.Y / scale);
        var map = _mutedLayerShowing && _mutedHitMap is not null ? _mutedHitMap : _hitMap;
        return map.IsOpaque(px, py);
    }

    /// <summary>The percent an x-coordinate in this control means, through the skin's fill range.</summary>
    public int PercentFromX(double x) =>
        SkinMath.PercentFromX(x / RenderScale, _info.FillStartX, _info.FillEndX);
}
