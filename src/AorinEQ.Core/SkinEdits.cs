namespace AorinEQ.Core;

/// <summary>Every change the Skin Designer can make to a skin, as a pure function from one
/// document to the next. The window never builds a document itself: it calls one of these and
/// commits the result to the history, so every edit is testable without a window and undo is
/// just "the previous document".
///
/// Every range goes through <see cref="SkinClamps"/> or the loader's own notice placement, so the
/// document after an edit is the document a save and a reload would produce. An edit that does not
/// apply (moving a text that is not there, clearing required art) returns the document unchanged,
/// which the history treats as a no-op.</summary>
public static class SkinEdits
{
    public static SkinDocument SetName(SkinDocument doc, string name) => doc with { Name = name };

    public static SkinDocument SetScale(SkinDocument doc, double scale) =>
        With(doc, doc.Config with { Scale = SkinClamps.Scale(scale) });

    public static SkinDocument SetFps(SkinDocument doc, double fps) =>
        With(doc, doc.Config with { Fps = SkinClamps.Fps(fps) });

    public static SkinDocument SetMutedDim(SkinDocument doc, double dim) =>
        With(doc, doc.Config with { MutedDim = SkinClamps.MutedDim(dim) });

    public static SkinDocument SetMeta(SkinDocument doc, SkinMeta meta) =>
        With(doc, doc.Config with { Meta = meta });

    /// <summary>Puts <paramref name="path"/> under an art layer as a sheet of
    /// <paramref name="frames"/> (a GIF is always one declared frame). New empty art of a
    /// different width drops the fill range - a range measured on other art means nothing; strip
    /// art re-measures the strip; notice art is placed where the loader places fresh art.</summary>
    public static SkinDocument SetArt(SkinDocument doc, SkinLayer layer, string path, int frames, ImageProbe probe)
    {
        int n = ImageProbe.IsGif(path) ? 1 : SkinClamps.Frames(frames);
        var c = doc.Config;
        switch (layer)
        {
            case SkinLayer.VolumeEmpty:
            {
                int before = VolumeSize(doc, probe).Width;
                var next = doc with { EmptyPath = path, Config = c with { EmptyFrames = n } };
                return VolumeSize(next, probe).Width == before
                    ? next
                    : With(next, next.Config with { FillStartX = null, FillEndX = null });
            }
            case SkinLayer.VolumeFull:
                return doc with { FullPath = path, Config = c with { FullFrames = n } };
            case SkinLayer.VolumeMuted:
                return doc with { MutedPath = path, Config = c with { MutedFrames = n } };
            case SkinLayer.AirPlayEmpty when c.AirPlay is { } strip:
                return With(doc, c with { AirPlay = SkinDocument.Restrip(strip with { EmptyPath = path, EmptyFrames = n }, probe) });
            case SkinLayer.AirPlayFull when c.AirPlay is { } strip:
                return With(doc, c with { AirPlay = SkinDocument.Restrip(strip with { FullPath = path, FullFrames = n }, probe) });
            case SkinLayer.NoticeArt when c.Notice is not null || doc.NoticeError is not null:
            {
                // New art for notice art that failed to load is the fix the issue asks for; it
                // starts from the default plate, as the loader dropped the failed one.
                var notice = c.Notice ?? SkinLoader.DefaultNotice;
                var art = probe.Layer(path, SkinLoader.NoticeLayer, n);
                return doc with
                {
                    NoticeError = null,
                    Config = c with
                    {
                        Notice = SkinLoader.PlaceNoticeArt(SkinLoader.ClampNotice(notice), path, art.IsGif, n,
                            art.LogicalWidth, art.LogicalHeight, null, null, null, null, null),
                    },
                };
            }
            default:
                return doc;
        }
    }

    /// <summary>A sheet's declared frame count. GIF layers ignore it (their frames come from the
    /// file), so it is a no-op for them.</summary>
    public static SkinDocument SetFrames(SkinDocument doc, SkinLayer layer, int frames, ImageProbe probe)
    {
        int n = SkinClamps.Frames(frames);
        var c = doc.Config;
        return layer switch
        {
            SkinLayer.VolumeEmpty when !ImageProbe.IsGif(doc.EmptyPath) => With(doc, c with { EmptyFrames = n }),
            SkinLayer.VolumeFull when !ImageProbe.IsGif(doc.FullPath) => With(doc, c with { FullFrames = n }),
            SkinLayer.VolumeMuted when doc.MutedPath is { } muted && !ImageProbe.IsGif(muted) =>
                With(doc, c with { MutedFrames = n }),
            SkinLayer.AirPlayEmpty when c.AirPlay is { EmptyIsGif: false } strip =>
                With(doc, c with { AirPlay = SkinDocument.Restrip(strip with { EmptyFrames = n }, probe) }),
            SkinLayer.AirPlayFull when c.AirPlay is { FullIsGif: false } strip =>
                With(doc, c with { AirPlay = SkinDocument.Restrip(strip with { FullFrames = n }, probe) }),
            SkinLayer.NoticeArt when c.Notice is { HasArt: true, ArtIsGif: false } notice =>
                With(doc, c with { Notice = SkinDocument.PlaceNotice(notice with { ArtFrames = n }, probe, out _) }),
            _ => doc,
        };
    }

    /// <summary>Takes away optional art: the muted layer (the empty layer is dimmed instead) or the
    /// notice art (the notice becomes a plate, its text and glyph back at the plate's positions -
    /// positions measured on art mean nothing on a plate). Required art cannot be cleared.</summary>
    public static SkinDocument ClearArt(SkinDocument doc, SkinLayer layer, ImageProbe probe) => layer switch
    {
        SkinLayer.VolumeMuted => doc with { MutedPath = null, Config = doc.Config with { MutedFrames = 1 } },
        SkinLayer.NoticeArt when doc.Config.Notice is { HasArt: true } notice =>
            With(doc, doc.Config with
            {
                Notice = SkinDocument.PlaceNotice(notice with
                {
                    ArtPath = null,
                    Text = notice.Text with { X = SkinLoader.DefaultNotice.Text.X, Y = SkinLoader.DefaultNotice.Text.Y },
                    Glyph = notice.Glyph with { X = SkinLoader.DefaultNotice.Glyph.X },
                }, probe, out _),
            }),
        _ => doc,
    };

    /// <summary>Where 0 % and 100 % sit on the volume bar or the strip, pulled into its art. A
    /// volume range covering the whole width is stored as no range, which is what the writer
    /// writes for it.</summary>
    public static SkinDocument SetFillRange(SkinDocument doc, SkinLayer bar, int start, int end, ImageProbe probe)
    {
        if (bar == SkinLayer.AirPlay && doc.Config.AirPlay is { } strip)
            return With(doc, doc.Config with
            {
                AirPlay = strip with
                {
                    FillStartX = SkinClamps.FillStart(start, strip.Width),
                    FillEndX = SkinClamps.FillEnd(end, strip.Width),
                },
            });
        if (bar != SkinLayer.VolumeBar) return doc;

        int width = VolumeSize(doc, probe).Width;
        int s = SkinClamps.FillStart(start, width), e = SkinClamps.FillEnd(end, width);
        bool whole = s == 0 && e == width;
        return With(doc, doc.Config with { FillStartX = whole ? null : s, FillEndX = whole ? null : e });
    }

    /// <summary>The text a text layer currently has, or null when it has none.</summary>
    public static SkinText? TextOf(SkinDocument doc, SkinLayer layer) => layer switch
    {
        SkinLayer.PercentText => doc.Config.Text,
        SkinLayer.AirPlayName => doc.Config.AirPlay?.Name,
        SkinLayer.AirPlayLevel => doc.Config.AirPlay?.Percent,
        SkinLayer.NoticeText => doc.Config.Notice?.Text,
        _ => null,
    };

    /// <summary>Replaces a text layer's text, clamped. A hidden text carrying nothing becomes no
    /// text; the notice text keeps only what a notice reads (no outline or shadow, always shown).</summary>
    public static SkinDocument SetText(SkinDocument doc, SkinLayer layer, SkinText? text, ImageProbe probe)
    {
        var c = doc.Config;
        switch (layer)
        {
            case SkinLayer.PercentText:
                return With(doc, c with { Text = SkinClamps.OptionalText(text) });
            case SkinLayer.AirPlayName when c.AirPlay is { } strip:
                return With(doc, c with { AirPlay = strip with { Name = SkinClamps.OptionalText(text) } });
            case SkinLayer.AirPlayLevel when c.AirPlay is { } strip:
                return With(doc, c with { AirPlay = strip with { Percent = SkinClamps.OptionalText(text) } });
            case SkinLayer.NoticeText when c.Notice is { } notice && text is not null:
            {
                var d = SkinLoader.DefaultNotice.Text;
                var noticeText = d with
                {
                    X = text.X,
                    Y = text.Y,
                    Color = SkinClamps.OrDefault(text.Color, d.Color),
                    FontFamily = SkinClamps.OrDefault(text.FontFamily, d.FontFamily),
                    FontSize = text.FontSize,
                    Bold = text.Bold,
                    Align = SkinClamps.Align(text.Align),
                };
                return With(doc, c with { Notice = SkinDocument.PlaceNotice(notice with { Text = noticeText }, probe, out _) });
            }
            default:
                return doc;
        }
    }

    /// <summary>Moves the percent text or an AirPlay label. Its anchor stays on its artboard, so a
    /// text dragged far off the art stops at the edge instead of becoming unreachable.</summary>
    public static SkinDocument MoveText(SkinDocument doc, SkinLayer layer, int dx, int dy, ImageProbe probe)
    {
        if (layer is not (SkinLayer.PercentText or SkinLayer.AirPlayName or SkinLayer.AirPlayLevel)
            || TextOf(doc, layer) is not { } text)
            return doc;
        var (width, height) = SkinLayers.BoardOf(layer) == SkinLayer.AirPlay
            ? (doc.Config.AirPlay!.Width, doc.Config.AirPlay.Height)
            : VolumeSize(doc, probe);
        if (width <= 0 || height <= 0) return doc; // no art to measure the board by: nothing to clamp into
        return SetText(doc, layer, text with
        {
            X = Math.Clamp(text.X + dx, 0, Math.Max(0, width)),
            Y = Math.Clamp(text.Y + dy, 0, Math.Max(0, height)),
        }, probe);
    }

    /// <summary>A strip from two images: measured from them, the whole strip opening the device
    /// list, the receiver's name left and its level right, both centred vertically.</summary>
    public static SkinDocument AddAirPlay(SkinDocument doc, string emptyPath, string fullPath, ImageProbe probe)
    {
        var seed = new SkinAirPlay(emptyPath, fullPath, 0, 0, false, false, 1, 1, 0, 0,
            null, null, new SkinHitRect(0, 0, 0, 0));
        var strip = SkinDocument.Restrip(seed, probe);
        int labelY = Math.Max(0, (strip.Height - (int)Math.Round(SkinClamps.DefaultFontSize * SkinClamps.LineHeightRatio)) / 2);
        strip = strip with
        {
            Name = new SkinText(true, Math.Min(10, strip.Width), labelY),
            Percent = new SkinText(true, Math.Max(0, strip.Width - 10), labelY, Align: "right"),
        };
        // A strip added over one that failed to load replaces it: the issue is resolved.
        return doc with { AirPlayError = null, Config = doc.Config with { AirPlay = strip } };
    }

    /// <summary>No strip. Also the explicit removal of a strip that failed to load, which is what
    /// lets that skin save again (and the save delete the broken strip files).</summary>
    public static SkinDocument RemoveAirPlay(SkinDocument doc) =>
        doc with { AirPlayError = null, Config = doc.Config with { AirPlay = null } };

    /// <summary>The dropdown or power-button region, pulled inside the strip. A null dropdown is the
    /// whole strip (the loader's default); a null or zero-area power region is no power button.</summary>
    public static SkinDocument SetHit(SkinDocument doc, SkinLayer layer, SkinHitRect? rect)
    {
        if (doc.Config.AirPlay is not { } strip) return doc;
        return layer switch
        {
            SkinLayer.AirPlayDropdown => With(doc, doc.Config with
            {
                AirPlay = strip with
                {
                    DropdownHit = rect is null
                        ? new SkinHitRect(0, 0, strip.Width, strip.Height)
                        : SkinClamps.Hit(rect, strip.Width, strip.Height),
                },
            }),
            SkinLayer.AirPlayPower => With(doc, doc.Config with
            {
                AirPlay = strip with { ConnectHit = rect is null ? null : SkinClamps.OptionalHit(rect, strip.Width, strip.Height) },
            }),
            _ => doc,
        };
    }

    /// <summary>A square power button at the strip's right-hand end, when it has none.</summary>
    public static SkinDocument AddPowerArea(SkinDocument doc)
    {
        if (doc.Config.AirPlay is not { ConnectHit: null } strip) return doc;
        int side = Math.Min(strip.Height, strip.Width);
        return SetHit(doc, SkinLayer.AirPlayPower, new SkinHitRect(Math.Max(0, strip.Width - side), 0, side, strip.Height));
    }

    public static SkinDocument SetConnectColor(SkinDocument doc, string? color) =>
        doc.Config.AirPlay is { } strip
            ? With(doc, doc.Config with { AirPlay = strip with { ConnectColor = SkinClamps.NullIfBlank(color) } })
            : doc;

    /// <summary>On: the skin describes its notice (starting from the default plate). Off: it
    /// describes none and the app draws its own. Either is the explicit decision about notice art
    /// that failed to load, so it clears that issue.</summary>
    public static SkinDocument SetNoticeEnabled(SkinDocument doc, bool enabled) =>
        doc with
        {
            NoticeError = null,
            Config = doc.Config with { Notice = enabled ? doc.Config.Notice ?? SkinLoader.DefaultNotice : null },
        };

    /// <summary>A whole notice look, clamped and placed by the loader's rules.</summary>
    public static SkinDocument SetNotice(SkinDocument doc, SkinNotice notice, ImageProbe probe) =>
        With(doc, doc.Config with { Notice = SkinDocument.PlaceNotice(notice, probe, out _) });

    /// <summary>A border switched on keeps the colour it had, or starts from the plate made opaque.</summary>
    public static SkinDocument SetNoticeBorder(SkinDocument doc, bool enabled) =>
        doc.Config.Notice is { } n
            ? With(doc, doc.Config with { Notice = n with { Border = enabled ? n.Border ?? SkinColor.Opaque(n.Background) : null } })
            : doc;

    /// <summary>Following: the icon is drawn in the text colour (no colour of its own is saved).
    /// Not following: it keeps the colour it was showing, so nothing jumps.</summary>
    public static SkinDocument SetNoticeGlyphFollowsText(SkinDocument doc, bool follows) =>
        doc.Config.Notice is { } n
            ? With(doc, doc.Config with { Notice = n with { Glyph = n.Glyph with { Color = follows ? null : n.Glyph.Color ?? n.Text.Color } } })
            : doc;

    /// <summary>Moves the notice text or icon on notice art, clamped into the art.</summary>
    public static SkinDocument MoveNoticeElement(SkinDocument doc, SkinLayer layer, int dx, int dy)
    {
        if (doc.Config.Notice is not { HasArt: true } n) return doc;
        return layer switch
        {
            SkinLayer.NoticeText => With(doc, doc.Config with { Notice = Replaced(n, n.Text.X + dx, n.Text.Y + dy, n.Glyph.X, n.Glyph.Y, n.TextWidth) }),
            SkinLayer.NoticeGlyph => With(doc, doc.Config with { Notice = Replaced(n, n.Text.X, n.Text.Y, n.Glyph.X + dx, n.Glyph.Y + dy, n.TextWidth) }),
            _ => doc,
        };
    }

    /// <summary>The notice text box's width on notice art, clamped to what is left of the art.</summary>
    public static SkinDocument SetNoticeTextWidth(SkinDocument doc, int width) =>
        doc.Config.Notice is { HasArt: true } n
            ? With(doc, doc.Config with { Notice = Replaced(n, n.Text.X, n.Text.Y, n.Glyph.X, n.Glyph.Y, width) })
            : doc;

    /// <summary>What a canvas drag of <paramref name="handle"/> on <paramref name="layer"/> by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) skin pixels makes of <paramref name="start"/>
    /// - always from the document at mouse-down, so a long drag cannot accumulate rounding. Fill
    /// handles never cross; areas keep their size while moving and never shrink below one pixel;
    /// art is select-only.</summary>
    public static SkinDocument Drag(SkinDocument start, SkinLayer layer, SkinHandle handle, int dx, int dy, ImageProbe probe)
    {
        if (handle is SkinHandle.FillStart or SkinHandle.FillEnd)
        {
            if (FillOf(start, layer, probe) is not { } fill) return start;
            return handle == SkinHandle.FillStart
                ? SetFillRange(start, layer, Math.Clamp(fill.Start + dx, 0, Math.Max(0, fill.End - 1)), fill.End, probe)
                : SetFillRange(start, layer, fill.Start, Math.Clamp(fill.End + dx, Math.Min(fill.Start + 1, fill.Width), fill.Width), probe);
        }

        switch (layer)
        {
            case SkinLayer.PercentText or SkinLayer.AirPlayName or SkinLayer.AirPlayLevel when handle == SkinHandle.Move:
                return MoveText(start, layer, dx, dy, probe);
            case SkinLayer.NoticeText when handle == SkinHandle.Right && start.Config.Notice is { } notice:
                return SetNoticeTextWidth(start, notice.TextWidth + dx);
            case SkinLayer.NoticeText or SkinLayer.NoticeGlyph when handle == SkinHandle.Move:
                return MoveNoticeElement(start, layer, dx, dy);
            case SkinLayer.AirPlayDropdown or SkinLayer.AirPlayPower
                when start.Config.AirPlay is { } strip
                     && (layer == SkinLayer.AirPlayDropdown ? strip.DropdownHit : strip.ConnectHit) is { } rect:
                return SetHit(start, layer, handle == SkinHandle.Move
                    ? rect with
                    {
                        X = Math.Clamp(rect.X + dx, 0, Math.Max(0, strip.Width - rect.Width)),
                        Y = Math.Clamp(rect.Y + dy, 0, Math.Max(0, strip.Height - rect.Height)),
                    }
                    : Inside(Resize(rect, handle, dx, dy), strip.Width, strip.Height));
            default:
                return start;
        }
    }

    /// <summary>A rectangle with one corner dragged; the opposite corner stays put and the
    /// rectangle never goes below 1×1 or turns inside out.</summary>
    public static SkinHitRect Resize(SkinHitRect r, SkinHandle handle, int dx, int dy)
    {
        int left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom;
        if (handle is SkinHandle.TopLeft or SkinHandle.BottomLeft) left = Math.Min(left + dx, right - 1);
        if (handle is SkinHandle.TopRight or SkinHandle.BottomRight) right = Math.Max(right + dx, left + 1);
        if (handle is SkinHandle.TopLeft or SkinHandle.TopRight) top = Math.Min(top + dy, bottom - 1);
        if (handle is SkinHandle.BottomLeft or SkinHandle.BottomRight) bottom = Math.Max(bottom + dy, top + 1);
        return new SkinHitRect(left, top, right - left, bottom - top);
    }

    /// <summary>A resized rectangle with each edge pulled inside the strip on its own, so an edge
    /// dragged past the strip stops at it and the edges not dragged stay where they were (clamping
    /// position and size together would move the far edge instead).</summary>
    private static SkinHitRect Inside(SkinHitRect r, int width, int height)
    {
        int left = Math.Max(0, r.X), top = Math.Max(0, r.Y);
        int right = Math.Clamp(r.Right, left, Math.Max(left, width)), bottom = Math.Clamp(r.Bottom, top, Math.Max(top, height));
        return new SkinHitRect(left, top, right - left, bottom - top);
    }

    private static SkinNotice Replaced(SkinNotice n, int textX, int textY, int glyphX, int glyphY, int textWidth) =>
        SkinLoader.PlaceNoticeArt(SkinLoader.ClampNotice(n), n.ArtPath!, n.ArtIsGif, n.ArtFrames, n.Width, n.Height,
            textX, textY, glyphX, glyphY, textWidth);

    private static (int Start, int End, int Width)? FillOf(SkinDocument doc, SkinLayer bar, ImageProbe probe)
    {
        if (bar == SkinLayer.AirPlay)
            return doc.Config.AirPlay is { } strip ? (strip.FillStartX, strip.FillEndX, strip.Width) : null;
        if (bar != SkinLayer.VolumeBar) return null;
        int width = VolumeSize(doc, probe).Width;
        return (SkinClamps.FillStart(doc.Config.FillStartX, width), SkinClamps.FillEnd(doc.Config.FillEndX, width), width);
    }

    /// <summary>The volume bar's logical frame size, or 0×0 while its empty art is unusable.</summary>
    private static (int Width, int Height) VolumeSize(SkinDocument doc, ImageProbe probe)
    {
        if (doc.EmptyPath.Length == 0) return (0, 0);
        var empty = probe.Layer(doc.EmptyPath, SkinLoader.EmptyLayer, doc.Config.EmptyFrames);
        return empty.IsUsable ? (empty.LogicalWidth, empty.LogicalHeight) : (0, 0);
    }

    private static SkinDocument With(SkinDocument doc, SkinConfig config) => doc with { Config = config };
}
