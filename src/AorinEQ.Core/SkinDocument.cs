using System.Globalization;

namespace AorinEQ.Core;

/// <summary>The skin the Skin Designer is editing, as one immutable value: its folder name, its
/// volume art (SOURCE paths, any folder; "" while not chosen) and the <see cref="SkinConfig"/>
/// the writer saves. Two documents with the same content are equal, which is what lets the
/// designer's history tell an edit from a no-op and a dirty skin from a saved one.
///
/// Everything a renderer needs is DERIVED, never stored: <see cref="ToInfo"/> probes the art and
/// applies the loader's own rules, so what the designer draws is what a save and a reload would
/// draw.
///
/// <paramref name="AirPlayError"/> and <paramref name="NoticeError"/> are the loader's reasons a
/// strip or notice the skin has on disk could not be loaded (the config then holds none). They are
/// kept, not dropped: each is a blocking <see cref="SkinIssue"/>, because a save would delete
/// that art. Only the edits that resolve them clear them (Task 5).</summary>
public sealed record SkinDocument(string Name, string EmptyPath, string FullPath, string? MutedPath, SkinConfig Config,
    string? AirPlayError = null, string? NoticeError = null)
{
    /// <summary>A loaded skin as a document. A full-width fill range becomes "no range" and a hidden
    /// text carrying nothing becomes no text - both what the writer would write.</summary>
    public static SkinDocument FromInfo(SkinInfo info)
    {
        bool wholeWidth = info.FillStartX == 0 && info.FillEndX == info.Width;
        var strip = info.AirPlay is { } a
            ? a with { Name = SkinClamps.OptionalText(a.Name), Percent = SkinClamps.OptionalText(a.Percent) }
            : null;
        return new SkinDocument(info.Name, info.EmptyPath, info.FullPath, info.MutedPath,
            new SkinConfig(SkinClamps.OptionalText(info.Text), info.Scale, info.Fps,
                info.EmptyFrames, info.FullFrames,
                wholeWidth ? null : info.FillStartX, wholeWidth ? null : info.FillEndX,
                info.MutedFrames, info.MutedDim, info.Meta, info.Notice, strip),
            strip is null ? info.AirPlayError : null,
            info.Notice is null ? info.NoticeError : null);
    }

    /// <summary>A skin with no art yet. The name comes from the caller so it can be localised.</summary>
    public static SkinDocument New(string name) =>
        new(name, "", "", null, new SkinConfig(null, SkinClamps.DefaultScale, Meta: SkinMeta.None));

    /// <summary>What <see cref="SkinLoader.Load"/> would return for this document once saved. One
    /// deliberate difference: while the volume art cannot be used, the strip and the notice are
    /// still derived (the loader drops them with the skin), so the designer keeps drawing them.</summary>
    public SkinInfo ToInfo(ImageProbe probe)
    {
        var c = Config;
        string folder = EmptyPath.Length == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(EmptyPath)) ?? "";
        var meta = c.Meta ?? SkinMeta.None;
        var airPlay = AirPlayInfo(probe, out var airPlayError);
        airPlayError ??= AirPlayError;
        string? noticeError = NoticeError;
        var placed = c.Notice is null ? null : PlaceNotice(c.Notice, probe, out noticeError);
        var notice = noticeError is null ? placed : null;

        SkinInfo Bad(string error) => new(Name, folder, EmptyPath, FullPath, 0, 0, c.Text, c.Scale, c.Fps,
            1, 1, false, false, 0, 0, null, false, 1, SkinClamps.DefaultMutedDim, meta, error,
            airPlay, airPlayError, notice, noticeError);

        if (EmptyPath.Length == 0) return Bad("empty artwork not chosen");
        if (FullPath.Length == 0) return Bad("full artwork not chosen");
        var empty = probe.Layer(EmptyPath, "empty", c.EmptyFrames);
        if (!empty.IsUsable) return Bad(empty.Error!);
        var full = probe.Layer(FullPath, "full", c.FullFrames);
        if (!full.IsUsable) return Bad(full.Error!);
        if (Mismatch(full, empty) is { } fullMismatch) return Bad(fullMismatch);

        bool mutedIsGif = false;
        if (MutedPath is not null)
        {
            var muted = probe.Layer(MutedPath, "muted", c.MutedFrames);
            if (!muted.IsUsable) return Bad(muted.Error!);
            if (Mismatch(muted, empty) is { } mutedMismatch) return Bad(mutedMismatch);
            mutedIsGif = muted.IsGif;
        }

        int start = SkinClamps.FillStart(c.FillStartX, empty.LogicalWidth);
        int end = SkinClamps.FillEnd(c.FillEndX, empty.LogicalWidth);
        if (SkinLoader.FillRangeError("", start, end) is { } fillError) return Bad(fillError);

        return new SkinInfo(Name, folder, empty.Path, full.Path, empty.LogicalWidth, empty.LogicalHeight,
            c.Text, c.Scale, c.Fps, empty.IsGif ? 1 : c.EmptyFrames, full.IsGif ? 1 : c.FullFrames,
            empty.IsGif, full.IsGif, start, end,
            MutedPath, mutedIsGif, MutedPath is null || mutedIsGif ? 1 : c.MutedFrames, c.MutedDim,
            meta, null, airPlay, airPlayError, notice, noticeError);
    }

    /// <summary>The config to hand <see cref="SkinWriter.Save"/>: the strip and notice placed
    /// against the art as it is on disk NOW, so the writer's "omit the default" checks (fill end
    /// at the strip's width, a whole-strip dropdown) compare against the real size.</summary>
    public SkinConfig SaveConfig(ImageProbe probe) => Config with
    {
        AirPlay = AirPlayInfo(probe, out _),
        Notice = Config.Notice is { } n ? PlaceNotice(n, probe, out _) : null,
    };

    /// <summary>Everything that stops this document saving, each on its layer. Layers measured
    /// against an unusable one (sizes compared with an unreadable empty layer) are skipped, so one
    /// broken file is one issue rather than five.</summary>
    public IReadOnlyList<SkinIssue> Issues(ImageProbe probe)
    {
        var issues = new List<SkinIssue>();
        var c = Config;

        if (SkinWriter.ValidateName(Name) is { } nameError)
            issues.Add(new SkinIssue(SkinLayer.SkinInfo, SkinIssueKind.InvalidName, [nameError]));

        var empty = Required(SkinLayer.VolumeEmpty, EmptyPath, "empty", c.EmptyFrames);
        var full = Required(SkinLayer.VolumeFull, FullPath, "full", c.FullFrames);
        var muted = MutedPath is null ? null : Probe(SkinLayer.VolumeMuted, MutedPath, "muted", c.MutedFrames);
        if (empty is { IsUsable: true } e)
        {
            MismatchIssue(SkinLayer.VolumeFull, full, e);
            MismatchIssue(SkinLayer.VolumeMuted, muted, e);
            FillIssue(SkinLayer.VolumeBar, c.FillStartX, c.FillEndX, e.LogicalWidth);
        }

        if (c.AirPlay is { } a)
        {
            var stripEmpty = Probe(SkinLayer.AirPlayEmpty, a.EmptyPath, "airplay-empty", a.EmptyFrames);
            var stripFull = Probe(SkinLayer.AirPlayFull, a.FullPath, "airplay-full", a.FullFrames);
            if (stripEmpty.IsUsable)
            {
                MismatchIssue(SkinLayer.AirPlayFull, stripFull, stripEmpty);
                FillIssue(SkinLayer.AirPlay, a.FillStartX, a.FillEndX, stripEmpty.LogicalWidth);
            }
        }

        if (c.Notice is { HasArt: true } n)
            Probe(SkinLayer.NoticeArt, n.ArtPath!, "notice", n.ArtIsGif ? 1 : n.ArtFrames);

        if (AirPlayError is { } stripError)
            issues.Add(new SkinIssue(SkinLayer.AirPlay, SkinIssueKind.AirPlayUnloadable, [stripError]));
        if (NoticeError is { } noticeError)
            issues.Add(new SkinIssue(SkinLayer.Notice, SkinIssueKind.NoticeUnloadable, [noticeError]));

        return issues;

        LayerProbe? Required(SkinLayer layer, string path, string label, int frames)
        {
            if (path.Length > 0) return Probe(layer, path, label, frames);
            issues.Add(new SkinIssue(layer, SkinIssueKind.MissingArt, []));
            return null;
        }

        LayerProbe Probe(SkinLayer layer, string path, string label, int frames)
        {
            var probed = probe.Layer(path, label, frames);
            if (probed.Problem is LayerProblem.NotFound or LayerProblem.Unreadable)
                issues.Add(new SkinIssue(layer, SkinIssueKind.ArtUnreadable, [Path.GetFileName(path)]));
            else if (probed.Problem == LayerProblem.FramesDoNotDivide)
                issues.Add(new SkinIssue(layer, SkinIssueKind.FramesDoNotDivide,
                    [Path.GetFileName(path), Number(probe.Size(path)?.Height ?? 0), Number(SkinClamps.Frames(frames))]));
            return probed;
        }

        void MismatchIssue(SkinLayer layer, LayerProbe? layerProbe, LayerProbe reference)
        {
            if (layerProbe is { IsUsable: true } p && !SkinLoader.SameFrame(p, reference))
                issues.Add(new SkinIssue(layer, SkinIssueKind.SizeMismatch,
                    [Path.GetFileName(p.Path), Number(p.LogicalWidth), Number(p.LogicalHeight),
                     Number(reference.LogicalWidth), Number(reference.LogicalHeight)]));
        }

        void FillIssue(SkinLayer layer, int? start, int? end, int width)
        {
            int s = SkinClamps.FillStart(start, width), f = SkinClamps.FillEnd(end, width);
            if (SkinLoader.FillRangeError("", s, f) is not null) issues.Add(new SkinIssue(layer, SkinIssueKind.FillRangeInverted, [Number(s), Number(f)]));
        }
    }

    /// <summary>A notice placed by the loader's own rules against its art as it is on disk now. A
    /// plate keeps its text position and glyph X (the loader reads them for a plate and the writer
    /// writes them back); its glyph Y is always 0, as the loader reads it. Art that cannot be read
    /// leaves the notice as it is, with the reason.</summary>
    internal static SkinNotice PlaceNotice(SkinNotice notice, ImageProbe probe, out string? error)
    {
        error = null;
        if (!notice.HasArt)
            return SkinLoader.ClampNotice(notice with
            {
                Glyph = notice.Glyph with { Y = 0 },
                ArtIsGif = false, ArtFrames = 1, Width = 0, Height = 0, TextWidth = 0,
            });

        int frames = SkinClamps.Frames(notice.ArtFrames);
        var art = probe.Layer(notice.ArtPath!, "notice", frames);
        if (!art.IsUsable)
        {
            error = art.Error;
            return notice;
        }
        return SkinLoader.PlaceNoticeArt(SkinLoader.ClampNotice(notice), art.Path, art.IsGif, art.IsGif ? 1 : frames,
            art.LogicalWidth, art.LogicalHeight, notice.Text.X, notice.Text.Y, notice.Glyph.X, notice.Glyph.Y,
            notice.TextWidth);
    }

    /// <summary>A strip re-measured against its art: size and GIF-ness from the headers, regions
    /// pulled inside it. Art of a different WIDTH resets the fill range to the full strip (a range
    /// measured on other art means nothing), and a dropdown that covered the whole strip keeps
    /// covering it.</summary>
    internal static SkinAirPlay Restrip(SkinAirPlay strip, ImageProbe probe)
    {
        var empty = probe.Layer(strip.EmptyPath, "airplay-empty", strip.EmptyFrames);
        var full = probe.Layer(strip.FullPath, "airplay-full", strip.FullFrames);
        int w = empty.LogicalWidth, h = empty.LogicalHeight;
        bool newWidth = w != strip.Width;
        bool wholeDropdown = strip.DropdownHit == new SkinHitRect(0, 0, strip.Width, strip.Height);
        return strip with
        {
            Width = w,
            Height = h,
            EmptyIsGif = empty.IsGif,
            FullIsGif = full.IsGif,
            EmptyFrames = empty.IsGif ? 1 : SkinClamps.Frames(strip.EmptyFrames),
            FullFrames = full.IsGif ? 1 : SkinClamps.Frames(strip.FullFrames),
            FillStartX = newWidth ? 0 : SkinClamps.FillStart(strip.FillStartX, w),
            FillEndX = newWidth ? w : SkinClamps.FillEnd(strip.FillEndX, w),
            DropdownHit = wholeDropdown ? new SkinHitRect(0, 0, w, h) : SkinClamps.Hit(strip.DropdownHit, w, h),
            ConnectHit = strip.ConnectHit is { } hit ? SkinClamps.OptionalHit(hit, w, h) : null,
        };
    }

    private SkinAirPlay? AirPlayInfo(ImageProbe probe, out string? error)
    {
        error = null;
        if (Config.AirPlay is not { } a) return null;
        var empty = probe.Layer(a.EmptyPath, "airplay-empty", a.EmptyFrames);
        if (!empty.IsUsable) { error = empty.Error; return null; }
        var full = probe.Layer(a.FullPath, "airplay-full", a.FullFrames);
        if (!full.IsUsable) { error = full.Error; return null; }
        error = SkinLoader.FrameMismatch("airplay-full", full, "airplay-empty", empty);
        if (error is not null) return null;
        var strip = Restrip(a, probe);
        error = SkinLoader.FillRangeError("airplay ", strip.FillStartX, strip.FillEndX);
        return error is null ? strip : null;
    }

    /// <summary>The loader's volume-layer size check, named by file as the loader names them.</summary>
    private static string? Mismatch(LayerProbe layer, LayerProbe reference) =>
        SkinLoader.FrameMismatch(Path.GetFileName(layer.Path), layer, Path.GetFileName(reference.Path), reference);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
