using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using AorinEQ.UI.DesignerFields;
using Button = System.Windows.Controls.Button;
using HAlign = System.Windows.HorizontalAlignment;

namespace AorinEQ.UI;

/// <summary>The Properties panel: hand-written, one builder per layer, out of the reusable field
/// controls. Every field reads the current document through a getter and writes through a
/// <see cref="SkinEdits"/> function; nothing here holds skin state.
///
/// The panel is REBUILT only when the selection or the document's shape changes (art added, the
/// strip removed); otherwise each field is refreshed in place, so typing, scrubbing and the colour
/// picker are never interrupted by the edit they just made.</summary>
public partial class SkinDesignerWindow
{
    private readonly List<Action> _propertyRefreshers = [];
    private string? _propertiesShape;
    private ColorPickerPopup? _picker;
    private ColorField? _pickerField;

    /// <summary>The one colour picker, created on first use.</summary>
    internal ColorPickerPopup? Picker => _picker;

    private void RefreshProperties(bool rebuild = false)
    {
        var shape = PropertiesShape();
        if (rebuild || shape != _propertiesShape)
        {
            _propertiesShape = shape;
            _propertyRefreshers.Clear();
            PropertiesHost.Children.Clear();
            foreach (var section in BuildProperties(_selection)) PropertiesHost.Children.Add(section);
        }
        foreach (var refresh in _propertyRefreshers) refresh();
    }

    /// <summary>What decides which fields exist. Sizes are NOT part of it: a frame count changes
    /// the frame size, and rebuilding then would replace the field being stepped or scrubbed. The
    /// ranges measured on art follow it in place instead (the NumberEntry taking a max getter).</summary>
    private string PropertiesShape()
    {
        var doc = _history.Current;
        var c = doc.Config;
        return string.Join("|", _selection, doc.AirPlayError is null, doc.NoticeError is null,
            doc.EmptyPath.Length == 0, doc.FullPath.Length == 0,
            ImageProbe.IsGif(doc.EmptyPath), ImageProbe.IsGif(doc.FullPath), doc.MutedPath is null,
            doc.MutedPath is { } muted && ImageProbe.IsGif(muted),
            c.AirPlay is null, c.AirPlay?.EmptyIsGif, c.AirPlay?.FullIsGif,
            c.AirPlay?.ConnectHit is null, c.Notice is null, c.Notice?.HasArt, c.Notice?.ArtIsGif);
    }

    private UIElement[] BuildProperties(SkinLayer layer) => layer switch
    {
        SkinLayer.SkinInfo => SkinInfoProperties(),
        SkinLayer.VolumeBar => VolumeBarProperties(),
        SkinLayer.VolumeEmpty or SkinLayer.VolumeFull or SkinLayer.VolumeMuted
            or SkinLayer.AirPlayEmpty or SkinLayer.AirPlayFull or SkinLayer.NoticeArt => ArtProperties(layer),
        SkinLayer.PercentText or SkinLayer.AirPlayName or SkinLayer.AirPlayLevel => TextProperties(layer),
        SkinLayer.AirPlay => AirPlayProperties(),
        SkinLayer.AirPlayDropdown or SkinLayer.AirPlayPower => AreaProperties(layer),
        SkinLayer.Notice => NoticeProperties(),
        SkinLayer.NoticeText => NoticeTextProperties(),
        _ => NoticeGlyphProperties(),
    };

    // ---- the layers ------------------------------------------------------------------------

    private UIElement[] SkinInfoProperties()
    {
        var sourceUrl = MetaEntry(Loc.T("designer.text.source-url-and-8212"), m => m.SourceUrl,
            (m, v) => SkinMeta.Create(m.Title, m.Author, m.Description, m.Version, m.Tags, v), SkinMeta.MaxSourceUrlLength,
            validate: v => v.Trim().Length == 0 || SkinMeta.Create(null, null, null, null, null, v).SourceUrl is not null);
        // A URL the normaliser would drop is refused here rather than silently emptied on save.
        sourceUrl.Rejected += () => StatusText.Text = Loc.T("designer.source-url.invalid");
        return
        [
            Section(Loc.T("designer.section.skin"),
                TextEntry(Loc.T("designer.prop.name"), d => d.Name, SkinEdits.SetName),
                NumberEntry(Loc.T("designer.text.scale"), SkinClamps.MinScale, SkinClamps.MaxScale,
                    d => d.Config.Scale, SkinEdits.SetScale, decimals: 2, step: 0.05),
                NumberEntry(Loc.T("designer.text.frames-per-second"), SkinClamps.MinFps, SkinClamps.MaxFps,
                    d => d.Config.Fps, SkinEdits.SetFps, decimals: 2)),
            Section(Loc.T("designer.text.details"),
                Hint(Loc.T("designer.text.optional-shown-in-the")),
                MetaEntry(Loc.T("designer.text.title-and-8212-the"), m => m.Title,
                    (m, v) => SkinMeta.Create(v, m.Author, m.Description, m.Version, m.Tags, m.SourceUrl), SkinMeta.MaxTitleLength),
                MetaEntry(Loc.T("designer.text.author-and-8212-who"), m => m.Author,
                    (m, v) => SkinMeta.Create(m.Title, v, m.Description, m.Version, m.Tags, m.SourceUrl), SkinMeta.MaxAuthorLength),
                MetaEntry(Loc.T("designer.text.description"), m => m.Description,
                    (m, v) => SkinMeta.Create(m.Title, m.Author, v, m.Version, m.Tags, m.SourceUrl), SkinMeta.MaxDescriptionLength,
                    multiline: true),
                MetaEntry(Loc.T("designer.text.version-and-8212-yours"), m => m.Version,
                    (m, v) => SkinMeta.Create(m.Title, m.Author, m.Description, v, m.Tags, m.SourceUrl), SkinMeta.MaxVersionLength),
                MetaEntry(Loc.T("designer.text.tags-and-8212-comma"), m => SkinMeta.FormatTags(m.Tags),
                    (m, v) => SkinMeta.Create(m.Title, m.Author, m.Description, m.Version, SkinMeta.ParseTags(v), m.SourceUrl), 0),
                sourceUrl),
            // One wording, shared with the status message Share writes.
            Section(Loc.T("designer.text.share"), Hint(SkinShare.HostingHint)),
        ];
    }

    private UIElement[] VolumeBarProperties()
    {
        var dim = NumberEntry(Loc.T("designer.text.mute-dim"), 0, 1, d => d.Config.MutedDim, SkinEdits.SetMutedDim,
            decimals: 2, step: 0.05);
        dim.ToolTip = Loc.T("designer.options-scroll.tooltip-4");
        dim.IsEnabled = _history.Current.MutedPath is null; // only means anything without muted art
        return
        [
            Section(Loc.T("designer.text.fill-range"),
                Hint(Loc.T("designer.text.where-the-bar-lives")),
                NumberEntry(Loc.T("designer.text.0-at"), 0, () => _info.Width, d => VolumeFill(d).Start,
                    (d, v) => SkinEdits.SetFillRange(d, SkinLayer.VolumeBar, (int)v, VolumeFill(d).End, _probe)),
                NumberEntry(Loc.T("designer.text.100-at"), 0, () => _info.Width, d => VolumeFill(d).End,
                    (d, v) => SkinEdits.SetFillRange(d, SkinLayer.VolumeBar, VolumeFill(d).Start, (int)v, _probe))),
            Section(Loc.T("designer.section.mute"), dim),
        ];
    }

    private (int Start, int End) VolumeFill(SkinDocument d) =>
        (SkinClamps.FillStart(d.Config.FillStartX, _info.Width), SkinClamps.FillEnd(d.Config.FillEndX, _info.Width));

    private UIElement[] ArtProperties(SkinLayer layer)
    {
        var doc = _history.Current;
        if (layer is SkinLayer.AirPlayEmpty or SkinLayer.AirPlayFull && doc.Config.AirPlay is null) return [];
        if (layer == SkinLayer.NoticeArt && doc.Config.Notice is null) return NoticeProperties();

        var art = new ArtField(optional: layer is SkinLayer.VolumeMuted or SkinLayer.NoticeArt);
        art.BrowseRequested += () => BrowseArt(layer);
        art.SheetRequested += () => BuildSheet(layer);
        art.RemoveRequested += () => Commit(SkinEdits.ClearArt(_history.Current, layer, _probe));
        _propertyRefreshers.Add(() =>
        {
            var (path, frames) = ArtOf(_history.Current, layer);
            art.ShowArt(path, ArtDisplayName(path, frames), FrameSize(path, frames));
        });

        var rows = new List<UIElement> { art };
        var (current, _) = ArtOf(doc, layer);
        bool gif = current is not null && ImageProbe.IsGif(current);
        if (layer != SkinLayer.NoticeArt) // the notice's frames live on the Notice layer
        {
            var frames = NumberEntry(Loc.T("designer.prop.frames"), 1, SkinFrames.MaxFrames, d => ArtOf(d, layer).Frames,
                (d, v) => SkinEdits.SetFrames(d, layer, (int)v, _probe));
            frames.IsEnabled = current is not null && !gif;
            rows.Add(frames);
        }
        if (gif) rows.Add(Hint(Loc.T("designer.art.gif-frames")));
        if (layer is SkinLayer.AirPlayEmpty or SkinLayer.AirPlayFull) rows.Add(Hint(Loc.T("designer.art.first-frame-only")));
        rows.Add(Hint(Loc.T("designer.text.png-sprite-sheet-png")));
        return [Section(Loc.T("designer.section.art"), [.. rows])];
    }

    private UIElement[] TextProperties(SkinLayer layer)
    {
        var doc = _history.Current;
        if (layer != SkinLayer.PercentText && doc.Config.AirPlay is null) return [];
        // Measured on the bar or the strip as they are now: a frame count changes the height.
        double Width() => layer == SkinLayer.PercentText ? _info.Width : _history.Current.Config.AirPlay?.Width ?? 0;
        double Height() => layer == SkinLayer.PercentText ? _info.Height : _history.Current.Config.AirPlay?.Height ?? 0;

        SkinText Current(SkinDocument d) => SkinEdits.TextOf(d, layer) ?? SkinText.Hidden;
        SkinDocument Change(SkinDocument d, Func<SkinText, SkinText> change) =>
            SkinEdits.SetText(d, layer, change(Current(d)), _probe);

        return
        [
            Section(Loc.T("designer.section.position"),
                ToggleEntry(Loc.T("designer.prop.show"), d => Current(d).Show, (d, on) => Change(d, t => t with { Show = on })),
                NumberEntry(Loc.T("designer.text.x"), 0, Width, d => Current(d).X, (d, v) => Change(d, t => t with { X = (int)v })),
                NumberEntry(Loc.T("designer.text.y"), 0, Height, d => Current(d).Y, (d, v) => Change(d, t => t with { Y = (int)v })),
                AlignEntry(d => Current(d).Align, (d, v) => Change(d, t => t with { Align = v }))),
            Section(Loc.T("designer.section.type"),
                FontEntry(Loc.T("designer.text.font"), d => Current(d).FontFamily, (d, v) => Change(d, t => t with { FontFamily = v })),
                NumberEntry(Loc.T("designer.text.size"), SkinClamps.MinFontSize, SkinClamps.MaxFontSize, d => Current(d).FontSize,
                    (d, v) => Change(d, t => t with { FontSize = v }), decimals: 1),
                ToggleEntry(Loc.T("designer.bold.label"), d => Current(d).Bold, (d, on) => Change(d, t => t with { Bold = on })),
                ColorEntry(Loc.T("designer.text.color"), d => Current(d).Color, (d, v) => Change(d, t => t with { Color = v }))),
            Section(Loc.T("designer.outline.label"),
                ToggleEntry(Loc.T("designer.prop.on"), d => Current(d).OutlineColor is not null,
                    (d, on) => Change(d, t => on ? Outlined(t, OutlineColor(t)) : t with { OutlineColor = null })),
                // Choosing a colour switches the outline on, as the old tick box did.
                ColorEntry(Loc.T("designer.text.color"), d => OutlineColor(Current(d)), (d, v) => Change(d, t => Outlined(t, v))),
                NumberEntry(Loc.T("designer.text.width"), 0, SkinClamps.MaxOutlineWidth, d => Current(d).OutlineWidth,
                    (d, v) => Change(d, t => t with { OutlineWidth = v }), decimals: 1)),
            Section(Loc.T("designer.shadow.label"),
                ToggleEntry(Loc.T("designer.prop.on"), d => Current(d).ShadowColor is not null,
                    (d, on) => Change(d, t => t with { ShadowColor = on ? ShadowColor(t) : null })),
                ColorEntry(Loc.T("designer.text.color"), d => ShadowColor(Current(d)),
                    (d, v) => Change(d, t => t with { ShadowColor = v })),
                NumberEntry(Loc.T("designer.text.blur"), 0, SkinClamps.MaxShadow, d => Current(d).ShadowBlur,
                    (d, v) => Change(d, t => t with { ShadowBlur = v }), decimals: 1),
                NumberEntry(Loc.T("designer.text.depth"), 0, SkinClamps.MaxShadow, d => Current(d).ShadowDepth,
                    (d, v) => Change(d, t => t with { ShadowDepth = v }), decimals: 1)),
        ];
    }

    private static string OutlineColor(SkinText t) => t.OutlineColor ?? SkinClamps.DefaultOutlineColor;

    private static string ShadowColor(SkinText t) => t.ShadowColor ?? SkinClamps.DefaultShadowColor;

    /// <summary>The outline switched on in <paramref name="color"/>, at its width or the default
    /// one when it had none.</summary>
    private static SkinText Outlined(SkinText t, string color) => t with
    {
        OutlineColor = color,
        OutlineWidth = t.OutlineWidth > 0 ? t.OutlineWidth : SkinClamps.DefaultOutlineWidth,
    };

    private UIElement[] AirPlayProperties()
    {
        if (_history.Current.AirPlayError is { } stripError)
            return [Section(LayerName(SkinLayer.AirPlay),
                Hint(new SkinIssue(SkinLayer.AirPlay, SkinIssueKind.AirPlayUnloadable, [stripError]).Message),
                ActionButton(Loc.T("designer.airplay.remove"), RemoveAirPlay),
                ActionButton(Loc.T("designer.layers.add-airplay"), AddAirPlay))];
        if (_history.Current.Config.AirPlay is null) return [];
        double Width() => _history.Current.Config.AirPlay?.Width ?? 0;
        return
        [
            Section(Loc.T("designer.text.fill-range"),
                NumberEntry(Loc.T("designer.text.0-at"), 0, Width, d => d.Config.AirPlay?.FillStartX ?? 0,
                    (d, v) => SkinEdits.SetFillRange(d, SkinLayer.AirPlay, (int)v, d.Config.AirPlay!.FillEndX, _probe)),
                NumberEntry(Loc.T("designer.text.100-at"), 0, Width, d => d.Config.AirPlay?.FillEndX ?? 0,
                    (d, v) => SkinEdits.SetFillRange(d, SkinLayer.AirPlay, d.Config.AirPlay!.FillStartX, (int)v, _probe))),
            Section(Loc.T("designer.section.power"),
                ColorEntry(Loc.T("designer.prop.power-color"),
                    d => d.Config.AirPlay?.ConnectColor ?? d.Config.AirPlay?.Name?.Color ?? SkinClamps.DefaultTextColor,
                    (d, v) => SkinEdits.SetConnectColor(d, v))),
            ActionButton(Loc.T("designer.airplay.remove"), RemoveAirPlay),
        ];
    }

    private UIElement[] AreaProperties(SkinLayer layer)
    {
        if (_history.Current.Config.AirPlay is not { } strip) return [];
        if (layer == SkinLayer.AirPlayPower && strip.ConnectHit is null)
            return [Section(Loc.T("designer.section.area"),
                ActionButton(Loc.T("designer.area.add"), () => Commit(SkinEdits.AddPowerArea(_history.Current))))];

        var rect = new RectField(Loc.T("designer.text.x"), Loc.T("designer.text.y"), Loc.T("designer.prop.width"),
            Loc.T("designer.prop.height"), strip.Width, strip.Height);
        var key = FieldKey();
        rect.ValueChanged += r => Commit(SkinEdits.SetHit(_history.Current, layer, r), key);
        rect.EditEnded += EndMerge;
        _propertyRefreshers.Add(() =>
        {
            var current = layer == SkinLayer.AirPlayDropdown ? _history.Current.Config.AirPlay?.DropdownHit : _history.Current.Config.AirPlay?.ConnectHit;
            if (_history.Current.Config.AirPlay is { } now) rect.SetArea(now.Width, now.Height);
            if (!rect.IsEditing && current is not null) rect.ShowValue(current);
        });
        var rows = new List<UIElement> { rect };
        if (layer == SkinLayer.AirPlayPower)
            rows.Add(ActionButton(Loc.T("designer.area.remove"),
                () => Commit(SkinEdits.SetHit(_history.Current, SkinLayer.AirPlayPower, null))));
        return [Section(Loc.T("designer.section.area"), [.. rows])];
    }

    private UIElement[] NoticeProperties()
    {
        var enabled = ToggleEntry(Loc.T("designer.notice.enable.label"), d => d.Config.Notice is not null, SkinEdits.SetNoticeEnabled);
        // Notice art the loader could not load: the user decides - off, or new art (switching the
        // toggle on starts a plate). Each clears the issue; none of them happens on its own.
        if (_history.Current.NoticeError is { } noticeError)
            return [Section(Loc.T("designer.text.notice"),
                Hint(new SkinIssue(SkinLayer.Notice, SkinIssueKind.NoticeUnloadable, [noticeError]).Message),
                ActionButton(Loc.T("designer.notice.turn-off"), () => Commit(SkinEdits.SetNoticeEnabled(_history.Current, false))),
                ActionButton(Loc.T("designer.notice.choose-art"), () => BrowseArt(SkinLayer.NoticeArt)),
                enabled)];
        if (_history.Current.Config.Notice is not { } notice)
            return [Section(Loc.T("designer.text.notice"), enabled, Hint(Loc.T("designer.notice.hint")))];

        SkinNotice Look(SkinDocument d) => d.Config.Notice ?? SkinLoader.DefaultNotice;
        SkinDocument Change(SkinDocument d, Func<SkinNotice, SkinNotice> change) => SkinEdits.SetNotice(d, change(Look(d)), _probe);

        var rows = new List<UIElement>
        {
            enabled,
            Hint(Loc.T("designer.notice.hint")),
            ColorEntry(Loc.T("designer.notice.background.label"), d => Look(d).Background, (d, v) => Change(d, n => n with { Background = v })),
            ToggleEntry(Loc.T("designer.notice.border.label"), d => Look(d).Border is not null, SkinEdits.SetNoticeBorder),
            // Choosing a border colour switches the border on.
            ColorEntry(Loc.T("designer.text.color"), d => Look(d).Border ?? SkinColor.Opaque(Look(d).Background),
                (d, v) => Change(d, n => n with { Border = v })),
            NumberEntry(Loc.T("designer.notice.radius.label"), 0, SkinClamps.MaxCornerRadius, d => Look(d).CornerRadius,
                (d, v) => Change(d, n => n with { CornerRadius = v }), decimals: 1),
            NumberEntry(Loc.T("designer.prop.padding-x"), 0, SkinClamps.MaxPadding, d => Look(d).PadX,
                (d, v) => Change(d, n => n with { PadX = v }), decimals: 1),
            NumberEntry(Loc.T("designer.prop.padding-y"), 0, SkinClamps.MaxPadding, d => Look(d).PadY,
                (d, v) => Change(d, n => n with { PadY = v }), decimals: 1),
        };
        if (notice.HasArt && !notice.ArtIsGif)
            rows.Add(NumberEntry(Loc.T("designer.prop.frames"), 1, SkinFrames.MaxFrames, d => Look(d).ArtFrames,
                (d, v) => SkinEdits.SetFrames(d, SkinLayer.NoticeArt, (int)v, _probe)));
        return [Section(Loc.T("designer.text.notice"), [.. rows])];
    }

    // The notice art's frame size as it is now (its frame count is edited on the Notice layer).
    private double NoticeWidth() => _history.Current.Config.Notice?.Width ?? 0;

    private double NoticeHeight() => _history.Current.Config.Notice?.Height ?? 0;

    private UIElement[] NoticeTextProperties()
    {
        if (_history.Current.Config.Notice is not { } notice) return NoticeProperties();
        SkinText Text(SkinDocument d) => d.Config.Notice?.Text ?? SkinLoader.DefaultNotice.Text;
        SkinDocument Change(SkinDocument d, Func<SkinText, SkinText> change) =>
            SkinEdits.SetText(d, SkinLayer.NoticeText, change(Text(d)), _probe);

        var type = Section(Loc.T("designer.section.type"),
            ColorEntry(Loc.T("designer.text.color"), d => Text(d).Color, (d, v) => Change(d, t => t with { Color = v })),
            FontEntry(Loc.T("designer.text.font"), d => Text(d).FontFamily, (d, v) => Change(d, t => t with { FontFamily = v })),
            NumberEntry(Loc.T("designer.text.size"), SkinClamps.MinFontSize, SkinClamps.MaxFontSize, d => Text(d).FontSize,
                (d, v) => Change(d, t => t with { FontSize = v }), decimals: 1),
            ToggleEntry(Loc.T("designer.bold.label"), d => Text(d).Bold, (d, on) => Change(d, t => t with { Bold = on })),
            AlignEntry(d => Text(d).Align, (d, v) => Change(d, t => t with { Align = v })));
        if (!notice.HasArt) return [type, Hint(Loc.T("designer.notice.positions-art-only"))];
        return
        [
            type,
            Section(Loc.T("designer.section.position"),
                NumberEntry(Loc.T("designer.text.x"), 0, NoticeWidth, d => Text(d).X, (d, v) => Change(d, t => t with { X = (int)v })),
                NumberEntry(Loc.T("designer.text.y"), 0, NoticeHeight, d => Text(d).Y, (d, v) => Change(d, t => t with { Y = (int)v })),
                NumberEntry(Loc.T("designer.prop.width"), 0, NoticeWidth, d => d.Config.Notice?.TextWidth ?? 0,
                    (d, v) => SkinEdits.SetNoticeTextWidth(d, (int)v))),
        ];
    }

    private UIElement[] NoticeGlyphProperties()
    {
        if (_history.Current.Config.Notice is not { } notice) return NoticeProperties();
        SkinNotice Look(SkinDocument d) => d.Config.Notice ?? SkinLoader.DefaultNotice;
        SkinDocument Change(SkinDocument d, Func<SkinNoticeGlyph, SkinNoticeGlyph> change) =>
            SkinEdits.SetNotice(d, Look(d) with { Glyph = change(Look(d).Glyph) }, _probe);

        var look = Section(Loc.T("designer.section.type"),
            ToggleEntry(Loc.T("designer.notice.show-glyph.label"), d => Look(d).Glyph.Show, (d, on) => Change(d, g => g with { Show = on })),
            NumberEntry(Loc.T("designer.notice.glyph-size.label"), SkinClamps.MinFontSize, SkinClamps.MaxFontSize,
                d => Look(d).Glyph.Size, (d, v) => Change(d, g => g with { Size = v }), decimals: 1),
            ToggleEntry(Loc.T("designer.notice.glyph-same.label"), d => Look(d).Glyph.Color is null, SkinEdits.SetNoticeGlyphFollowsText),
            ColorEntry(Loc.T("designer.text.color"), d => Look(d).Glyph.Color ?? Look(d).Text.Color,
                (d, v) => Change(d, g => g with { Color = v })));
        if (!notice.HasArt) return [look, Hint(Loc.T("designer.notice.positions-art-only"))];
        return
        [
            look,
            Section(Loc.T("designer.section.position"),
                NumberEntry(Loc.T("designer.text.x"), 0, NoticeWidth, d => Look(d).Glyph.X, (d, v) => Change(d, g => g with { X = (int)v })),
                NumberEntry(Loc.T("designer.text.y"), 0, NoticeHeight, d => Look(d).Glyph.Y, (d, v) => Change(d, g => g with { Y = (int)v }))),
        ];
    }

    // ---- art -------------------------------------------------------------------------------

    private static (string? Path, int Frames) ArtOf(SkinDocument d, SkinLayer layer) => layer switch
    {
        SkinLayer.VolumeEmpty => (d.EmptyPath.Length == 0 ? null : d.EmptyPath, d.Config.EmptyFrames),
        SkinLayer.VolumeFull => (d.FullPath.Length == 0 ? null : d.FullPath, d.Config.FullFrames),
        SkinLayer.VolumeMuted => (d.MutedPath, d.Config.MutedFrames),
        SkinLayer.AirPlayEmpty => (d.Config.AirPlay?.EmptyPath, d.Config.AirPlay?.EmptyFrames ?? 1),
        SkinLayer.AirPlayFull => (d.Config.AirPlay?.FullPath, d.Config.AirPlay?.FullFrames ?? 1),
        SkinLayer.NoticeArt => (d.Config.Notice?.ArtPath, d.Config.Notice?.ArtFrames ?? 1),
        _ => (null, 1),
    };

    private string ArtDisplayName(string? path, int frames) =>
        path is null ? Loc.T("designer.empty-path.text")
        : path.StartsWith(_scratchFolder, StringComparison.OrdinalIgnoreCase) ? Loc.T("designer.art.frames-sheet", frames)
        : Path.GetFileName(path);

    private (int Width, int Height)? FrameSize(string? path, int frames) =>
        path is not null && _probe.Layer(path, Path.GetFileNameWithoutExtension(path), frames) is { IsUsable: true } p
            ? (p.LogicalWidth, p.LogicalHeight)
            : null;

    private string? AskImage(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ImageFilter, Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void BrowseArt(SkinLayer layer)
    {
        var title = layer switch
        {
            SkinLayer.VolumeEmpty => Loc.T("designer.choose-the-empty-layer-0-artwork"),
            SkinLayer.VolumeFull => Loc.T("designer.choose-the-full-layer-100-artwork"),
            SkinLayer.VolumeMuted => Loc.T("designer.choose-the-muted-artwork-shown-instead"),
            SkinLayer.AirPlayEmpty => Loc.T("designer.choose-airplay-empty"),
            SkinLayer.AirPlayFull => Loc.T("designer.choose-airplay-full"),
            _ => Loc.T("designer.choose-the-notice-artwork"),
        };
        if (AskImage(title) is { } path) Commit(SkinEdits.SetArt(_history.Current, layer, path, 1, _probe));
    }

    /// <summary>Builds a sprite sheet from a numbered PNG frame sequence (the Photoshop "Export
    /// Layers to Files" workflow): frames sorted by file name become the layer, its frame count
    /// filled in.</summary>
    private void BuildSheet(SkinLayer layer)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG frames (*.png)|*.png",
            Multiselect = true,
            Title = Loc.T("designer.select-the-frames-in-order-sorted"),
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;
        var files = dialog.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (WriteSheet(files) is not { } sheet) return;
        Commit(SkinEdits.SetArt(_history.Current, layer, sheet, files.Length, _probe));
        StatusText.Text = Loc.T("designer.status.assembled-frames-into-a", files.Length);
    }

    /// <summary>Stacks equal-sized PNG frames into one sheet in this session's scratch folder
    /// (deleted by <see cref="Dispose"/>). Null, with the reason in the status line, when a frame
    /// is not a PNG, the sizes differ, or a frame will not decode.</summary>
    internal string? WriteSheet(IReadOnlyList<string> files)
    {
        try
        {
            (int Width, int Height)? size = null;
            foreach (var file in files)
            {
                var frame = PngHeader.Read(file);
                if (frame is null)
                {
                    StatusText.Text = Path.GetFileName(file) + Loc.T("designer.is-not-a-valid-png");
                    return null;
                }
                if (size is not null && frame != size)
                {
                    StatusText.Text = Loc.T("designer.is-but-earlier-frames-are-all", Path.GetFileName(file),
                        frame.Value.Width, frame.Value.Height, size.Value.Width, size.Value.Height);
                    return null;
                }
                size ??= frame;
            }

            var (w, h) = size!.Value;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                for (int i = 0; i < files.Count; i++)
                    dc.DrawImage(SkinOsdWindow.LoadBitmap(files[i]), new Rect(0, i * h, w, h));
            var sheet = new RenderTargetBitmap(w, h * files.Count, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual);
            Directory.CreateDirectory(_scratchFolder);
            var path = Path.Combine(_scratchFolder, Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using (var stream = File.Create(path))
                encoder.Save(stream);
            return path;
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            StatusText.Text = Loc.T("designer.image-failed-to-decode") + ex.Message;
            return null;
        }
    }

    // ---- the colour picker -----------------------------------------------------------------

    /// <summary>Opens the picker on <paramref name="field"/>. Open first ends the previous opening -
    /// its pending hex committed, which raises ColorChanged for the PREVIOUS field - so the picker
    /// is retargeted only once Open returns. The merge is ended between the two, so each opening is
    /// one step in the history even when the same field is picked twice in a row.</summary>
    private void OpenPicker(ColorField field)
    {
        _picker ??= CreatePicker();
        _picker.Open(field.Swatch, field.Value, _currentSettings().RecentColors ?? []);
        EndMerge();
        _pickerField = field;
    }

    private ColorPickerPopup CreatePicker()
    {
        var picker = new ColorPickerPopup { EyedropperAvailable = false };
        picker.ColorChanged += OnPickerColorChanged;
        picker.Closed += OnPickerClosed;
        picker.RecentsChanged += OnPickerRecentsChanged;
        return picker;
    }

    private void OnPickerColorChanged(string hex) => _pickerField?.ApplyPicked(hex);

    /// <summary>Raised a turn after the popup closes - after a reopen that cancelled the close it
    /// would end the NEW opening, so it only acts while the picker is really shut. It applies
    /// nothing: the picker commits its pending hex before raising this.</summary>
    private void OnPickerClosed(object? sender, EventArgs e)
    {
        if (_picker is { IsOpen: true }) return;
        EndMerge();
        _pickerField = null;
    }

    private void OnPickerRecentsChanged(IReadOnlyList<string> colours) => RecentColorsChanged?.Invoke(colours);

    /// <summary>Ends the opening now - its pending hex applied to its field and the colour
    /// remembered - and closes the picker: the field it edits is about to go (another selection,
    /// another document, the window disposed), so nothing may wait for the late Closed.</summary>
    private void ClosePicker()
    {
        if (_picker is null || _pickerField is null) return;
        _picker.Commit();
        _picker.IsOpen = false;
        EndMerge();
        _pickerField = null;
    }

    // ---- building blocks -------------------------------------------------------------------

    /// <summary>A merge key unique to one field of one panel: consecutive edits through the same
    /// field (typing, scrubbing, arrow steps, one picker opening) are one undo step.</summary>
    private string FieldKey() => $"{_selection}:{_propertyRefreshers.Count}";

    private NumberField NumberEntry(string label, double min, double max, Func<SkinDocument, double> get,
        Func<SkinDocument, double, SkinDocument> set, int decimals = 0, double step = 1) =>
        NumberEntry(label, min, () => max, get, set, decimals, step);

    /// <summary>A number whose upper bound is measured on art that can change size while the field
    /// is shown: <paramref name="max"/> is read again on every refresh and the range follows.</summary>
    private NumberField NumberEntry(string label, double min, Func<double> max, Func<SkinDocument, double> get,
        Func<SkinDocument, double, SkinDocument> set, int decimals = 0, double step = 1)
    {
        var field = new NumberField(label, min, max(), decimals, step);
        var key = FieldKey();
        field.ValueChanged += v => Commit(set(_history.Current, v), key);
        field.EditEnded += EndMerge;
        _propertyRefreshers.Add(() =>
        {
            field.SetRange(min, max());
            if (!field.IsEditing) field.ShowValue(get(_history.Current));
        });
        return field;
    }

    private TextField TextEntry(string label, Func<SkinDocument, string> get, Func<SkinDocument, string, SkinDocument> set,
        bool multiline = false, int maxLength = 0, Func<string, bool>? validate = null)
    {
        var field = new TextField(label, multiline, maxLength, validate);
        var key = FieldKey();
        field.ValueChanged += v => Commit(set(_history.Current, v), key);
        field.EditEnded += EndMerge;
        _propertyRefreshers.Add(() => { if (!field.IsEditing) field.ShowValue(get(_history.Current)); });
        return field;
    }

    private TextField MetaEntry(string label, Func<SkinMeta, string?> get, Func<SkinMeta, string, SkinMeta> set,
        int maxLength, bool multiline = false, Func<string, bool>? validate = null) =>
        TextEntry(label, d => get(d.Config.Meta ?? SkinMeta.None) ?? "",
            (d, v) => SkinEdits.SetMeta(d, set(d.Config.Meta ?? SkinMeta.None, v)), multiline, maxLength, validate);

    private ToggleField ToggleEntry(string label, Func<SkinDocument, bool> get, Func<SkinDocument, bool, SkinDocument> set)
    {
        var field = new ToggleField(label);
        field.Toggled += on =>
        {
            EndMerge();
            Commit(set(_history.Current, on));
        };
        _propertyRefreshers.Add(() => field.ShowValue(get(_history.Current)));
        return field;
    }

    private ColorField ColorEntry(string label, Func<SkinDocument, string> get, Func<SkinDocument, string, SkinDocument> set)
    {
        var field = new ColorField(label);
        var key = FieldKey();
        field.ValueChanged += v => Commit(set(_history.Current, v), key);
        field.EditEnded += EndMerge;
        field.PickRequested += OpenPicker;
        _propertyRefreshers.Add(() => { if (!field.IsEditing) field.ShowValue(get(_history.Current)); });
        return field;
    }

    private FontField FontEntry(string label, Func<SkinDocument, string> get, Func<SkinDocument, string, SkinDocument> set)
    {
        var field = new FontField(label);
        field.ValueChanged += v =>
        {
            EndMerge();
            Commit(set(_history.Current, v));
        };
        _propertyRefreshers.Add(() => { if (!field.IsEditing) field.ShowValue(get(_history.Current)); });
        return field;
    }

    private ChoiceField AlignEntry(Func<SkinDocument, string> get, Func<SkinDocument, string, SkinDocument> set)
    {
        var field = new ChoiceField(Loc.T("designer.text.align"),
        [
            (Loc.T("designer.align.left"), "left"),
            (Loc.T("designer.align.center"), "center"),
            (Loc.T("designer.align.right"), "right"),
        ]);
        field.ToolTip = Loc.T("designer.text-style.tooltip");
        field.ValueChanged += v =>
        {
            EndMerge();
            Commit(set(_history.Current, v));
        };
        _propertyRefreshers.Add(() => field.ShowValue(get(_history.Current)));
        return field;
    }

    private static Expander Section(string header, params UIElement[] rows)
    {
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var row in rows)
        {
            if (row is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(row);
        }
        return new Expander { Header = header, IsExpanded = true, Content = body, Margin = new Thickness(0, 0, 0, 8) };
    }

    private static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return hint;
    }

    private static Button ActionButton(string label, Action run)
    {
        var button = new Button { Content = label, HorizontalAlignment = HAlign.Left, Padding = new Thickness(10, 4, 10, 4) };
        button.Click += (_, _) => run();
        return button;
    }
}
