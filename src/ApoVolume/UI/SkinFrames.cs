using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApoVolume.UI;

/// <summary>One skin layer decoded into its frames — the single internal representation behind
/// all three input formats: a static PNG (1 frame), a sprite-sheet PNG sliced by the declared
/// frame count (uniform delay from fps), or a GIF (frames + per-frame delays from the file).
/// All frames are frozen and sized to the layer's logical frame size.</summary>
internal sealed class SkinFrames
{
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
        BitmapSource? canvas = null;
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

            BitmapSource composed;
            bool coversCanvas = left == 0 && top == 0
                && frame.PixelWidth == width && frame.PixelHeight == height;
            if (coversCanvas)
            {
                // Full-canvas frame: treat as a replacement. (GIF disposal modes are approximated:
                // partial frames composite over the previous canvas, full frames replace it —
                // correct for the overwhelming majority of real GIFs, and "restore to background"
                // is not distinguished. Documented limitation.)
                composed = frame;
            }
            else
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    if (canvas is not null)
                        dc.DrawImage(canvas, new Rect(0, 0, width, height));
                    dc.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
                }
                var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();
                composed = target;
            }
            frames.Add(composed);
            delays.Add(TimeSpan.FromMilliseconds(delayCentiseconds * 10));
            canvas = composed;
        }
        return new SkinFrames(frames, delays);
    }
}
