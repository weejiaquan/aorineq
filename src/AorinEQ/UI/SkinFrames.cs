using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AorinEQ.UI;

/// <summary>One skin layer decoded into its frames — the single internal representation behind
/// all three input formats: a static PNG (1 frame), a sprite-sheet PNG sliced by the declared
/// frame count (uniform delay from fps), or a GIF (frames + per-frame delays from the file).
/// All frames are frozen and sized to the layer's logical frame size.</summary>
internal sealed class SkinFrames
{
    /// <summary>Upper bound per layer: shared skins are untrusted input, and every decoded frame
    /// is held in memory (plus one union alpha map) — a runaway GIF/sheet must fail with a clear
    /// message instead of allocating without limit.</summary>
    public const int MaxFrames = 120;

    public IReadOnlyList<BitmapSource> Frames { get; }
    public IReadOnlyList<TimeSpan> Delays { get; }
    public bool IsAnimated => Frames.Count > 1;

    private SkinFrames(List<BitmapSource> frames, List<TimeSpan> delays)
    {
        Frames = frames;
        Delays = delays;
    }

    /// <summary>Decode failures throw the imaging exception family callers already contain
    /// (NotSupportedException/FileFormatException/IOException/ArgumentException).</summary>
    public static SkinFrames Load(string path, int declaredFrames, double fps)
    {
        return Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? LoadGif(path)
            : LoadSheet(path, declaredFrames, fps);
    }

    private static SkinFrames LoadSheet(string path, int declaredFrames, double fps)
    {
        if (declaredFrames > MaxFrames)
            throw new NotSupportedException(
                $"{Path.GetFileName(path)} declares {declaredFrames} frames — the limit is {MaxFrames}.");
        var bmp = SkinOsdWindow.LoadBitmap(path);
        var delay = TimeSpan.FromSeconds(1.0 / fps);
        if (declaredFrames <= 1)
            return new SkinFrames(new List<BitmapSource> { bmp }, new List<TimeSpan> { delay });

        int frameHeight = bmp.PixelHeight / declaredFrames;
        var frames = new List<BitmapSource>(declaredFrames);
        var delays = new List<TimeSpan>(declaredFrames);
        for (int i = 0; i < declaredFrames; i++)
        {
            var crop = new CroppedBitmap(bmp, new Int32Rect(0, i * frameHeight, bmp.PixelWidth, frameHeight));
            crop.Freeze();
            frames.Add(crop);
            delays.Add(delay);
        }
        return new SkinFrames(frames, delays);
    }

    private static SkinFrames LoadGif(string path)
    {
        // OnLoad + IgnoreImageCache for the same reasons as LoadBitmap: release the file handle
        // immediately and never serve stale cached bytes after an in-place edit.
        var decoder = new GifBitmapDecoder(new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new NotSupportedException($"{Path.GetFileName(path)} contains no frames.");
        if (decoder.Frames.Count > MaxFrames)
            throw new NotSupportedException(
                $"{Path.GetFileName(path)} has {decoder.Frames.Count} frames — the limit is {MaxFrames}.");

        // Logical screen size; fall back to the first frame's size when metadata is absent.
        int width = decoder.Frames[0].PixelWidth;
        int height = decoder.Frames[0].PixelHeight;
        if (decoder.Metadata is { } gifMeta)
        {
            if (gifMeta.ContainsQuery("/logscrdesc/Width"))
                width = Convert.ToInt32(gifMeta.GetQuery("/logscrdesc/Width"));
            if (gifMeta.ContainsQuery("/logscrdesc/Height"))
                height = Convert.ToInt32(gifMeta.GetQuery("/logscrdesc/Height"));
        }

        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        BitmapSource? canvas = null; // the base the NEXT frame composites onto
        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            int delayCentiseconds = 10;
            if (meta is not null && meta.ContainsQuery("/grctlext/Delay"))
                delayCentiseconds = Convert.ToInt32(meta.GetQuery("/grctlext/Delay"));
            if (delayCentiseconds <= 1)
                delayCentiseconds = 10; // missing/zero delay: the 100 ms de-facto standard

            int left = 0, top = 0;
            if (meta is not null && meta.ContainsQuery("/imgdesc/Left"))
                left = Convert.ToInt32(meta.GetQuery("/imgdesc/Left"));
            if (meta is not null && meta.ContainsQuery("/imgdesc/Top"))
                top = Convert.ToInt32(meta.GetQuery("/imgdesc/Top"));
            // GIF disposal method (GCE packed field): 0/1 keep, 2 restore-to-background
            // (clear the frame's rect), 3 restore-to-previous (revert the whole frame).
            int disposal = 0;
            if (meta is not null && meta.ContainsQuery("/grctlext/Disposal"))
                disposal = Convert.ToInt32(meta.GetQuery("/grctlext/Disposal"));

            var frameRect = new Rect(left, top, frame.PixelWidth, frame.PixelHeight);
            bool coversCanvas = left == 0 && top == 0
                && frame.PixelWidth == width && frame.PixelHeight == height;

            // What the viewer sees for this frame: the frame over the current base.
            BitmapSource composed = coversCanvas && canvas is null
                ? frame
                : Render(width, height, dc =>
                {
                    if (canvas is not null)
                        dc.DrawImage(canvas, new Rect(0, 0, width, height));
                    dc.DrawImage(frame, frameRect);
                });
            frames.Add(composed);
            delays.Add(TimeSpan.FromMilliseconds(delayCentiseconds * 10));

            // The base for the NEXT frame, per this frame's disposal method.
            canvas = disposal switch
            {
                // Restore to background: GIF "background" is transparent for our purposes —
                // clear this frame's rect out of the composed image.
                2 => Render(width, height, dc =>
                {
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, width, height)),
                        new RectangleGeometry(frameRect)));
                    dc.DrawImage(composed, new Rect(0, 0, width, height));
                    dc.Pop();
                }),
                // Restore to previous: the next frame composites onto what was there BEFORE this one.
                3 => canvas,
                _ => composed, // 0/1: keep what the viewer saw
            };
        }
        return new SkinFrames(frames, delays);
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            draw(dc);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
