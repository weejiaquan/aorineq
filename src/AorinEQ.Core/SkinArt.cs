using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AorinEQ.Core;

/// <summary>The artwork for the four skins AorinEQ ships with: its own house style, and three that
/// mimic the volume OSD of Windows 11, Windows 10 and Windows XP.
///
/// DRAWN, not authored in an image editor, for the same reason <see cref="AppIconArt"/> is: art
/// that lives as code can be regenerated, reviewed in a diff and tested. It also means the shipped
/// skins cost the installer nothing - <see cref="DefaultSkins"/> renders them into the user's
/// skins folder on first run, so a single-file exe carries no image payload at all.
///
/// EACH STYLE HAS ITS OWN LAYOUT, not one shape recoloured. That was the first version's mistake:
/// three palettes over one 300x64 rounded box share nothing with the things they are named after.
/// A Windows 10 flyout is square, opaque, and leads with an icon block and a big number; a Windows
/// 11 one is rounded, translucent and compact; XP had no volume OSD at all, so the reference is its
/// tray volume popup - a Luna panel with a trackbar and a Mute checkbox. <see cref="Layout"/> is
/// what makes them able to differ: size, fill range and every text anchor come from it, and
/// <see cref="SkinJson"/> is generated from the same record the drawing reads.
///
/// WHAT THE FORMAT CAN AND CANNOT DRAW. The renderer reveals the full layer through a rectangular
/// clip running from fillStartX to the current level across the FULL HEIGHT of the image (see
/// SkinView and SkinComposite.ComplementClip). Two consequences shape every layout here:
///
///   - A slider THUMB that tracks the level is not expressible. Nothing about the artwork depends
///     on the level except which side of one vertical edge a pixel is on. This costs nothing in
///     fidelity: the real Windows volume OSD is a plain filled bar in both 10 and 11 - the knob
///     belongs to the taskbar flyout, which is a different control.
///   - Anything the empty layer draws between fillStartX and fillEndX is replaced by the full
///     layer as the level rises. So glyphs and labels sit OUTSIDE the fill range, and both layers
///     paint the whole image and differ only inside the track.
///
/// Each style draws FIVE layers. empty/full are the volume bar every skin has had since 1.0.
/// muted is optional in the format and drawn here because each of these has a real way to say it -
/// a struck-through speaker, or XP's Mute checkbox ticked - which beats the generic dimming a skin
/// without one falls back to. airplay-empty/airplay-full are the strip added alongside the OSD's
/// AirPlay control, and they are the reference for what an AirPlay bar looks like in a format a
/// skin author can copy.</summary>
public static class SkinArt
{
    /// <summary>The skin the OSD uses when the style is "custom skin" but no skin has been picked.
    /// A named default rather than a silent fall back to the built-in dark pill, which is what
    /// choosing "custom skin" and getting the un-skinned OSD used to look like.</summary>
    public const string Default = "aorineq";

    public static readonly IReadOnlyList<string> All = [Default, "windows-11", "windows-10", "windows-xp"];

    /// <summary>The skin folder a "custom skin" OSD should load. An empty name means the user has
    /// never picked one, which used to render as the built-in dark pill - the one style that looks
    /// like nothing was applied. It resolves to <see cref="Default"/> instead, so choosing the
    /// style shows a skin.
    ///
    /// Resolution only. Nothing here writes settings: the name on disk stays empty until the user
    /// picks something, so a later change to what the default IS still reaches them.</summary>
    public static string Resolve(string? skinName) =>
        string.IsNullOrWhiteSpace(skinName) ? Default : skinName;

    /// <summary>Every layer <see cref="Draw"/> understands, in the order DefaultSkins writes them.
    /// muted is optional to the loader but not to these: all four ship one.</summary>
    public static readonly IReadOnlyList<string> Layers =
        ["empty", "full", "muted", "airplay-empty", "airplay-full"];

    /// <summary>A rectangle, in skin coordinates. Its own type so a layout's four boxes cannot be
    /// swapped for each other by argument order.</summary>
    public readonly record struct Box(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
        public float CenterX => X + W / 2f;
        public float CenterY => Y + H / 2f;
        public Rectangle ToRect() => new(X, Y, W, H);
    }

    /// <summary>Where everything is, for one style. The ONE source: the drawing below reads it and
    /// <see cref="SkinJson"/> is generated from it, so a fill range that moves in the artwork
    /// cannot silently disagree with the JSON that describes it.</summary>
    /// <param name="Width">Both layers' width. The volume bar and the AirPlay strip share it so the
    /// strip lines up under the bar rather than sticking out past it.</param>
    /// <param name="VolumeHeight">Height of empty/full/muted.</param>
    /// <param name="AirPlayHeight">Height of the two airplay layers.</param>
    /// <param name="Track">The volume bar's lit band. Its X..Right IS the fill range.</param>
    /// <param name="Glyph">Where the speaker is drawn. Always outside the fill range.</param>
    /// <param name="PercentX">The percent number's anchor, in the alignment given by
    /// <paramref name="PercentAlign"/>.</param>
    /// <param name="AirTrack">The AirPlay strip's lit band, and its fill range.</param>
    /// <param name="ConnectHit">The power button's box - what the skin declares as connectHit, so
    /// one tap connects or disconnects without going through the list. The GLYPH inside it is drawn
    /// by the app, not by the artwork, because it changes with the session and a PNG cannot; the
    /// skin reserves the space and says what colour it is when lit.</param>
    /// <param name="DropdownHit">The chevron's box - what the skin declares as dropdownHit so a
    /// click there opens the device list instead of setting the receiver's volume.</param>
    public sealed record Layout(
        int Width, int VolumeHeight, int AirPlayHeight,
        Box Track, Box Glyph,
        int PercentX, int PercentY, string PercentAlign, int PercentSize,
        Box AirTrack,
        int NameX, int NameY, int LevelX, int LevelY, int LabelSize,
        Box ConnectHit, Box DropdownHit);

    /// <summary>The one place a style's colours and metrics are written down.</summary>
    private sealed record Palette(
        Color Face, Color Edge, Color Track, Color Accent, Color Ink,
        int Corner, float EdgeWidth, string Font);

    public static Layout LayoutFor(string style) => style switch
    {
        // Windows 11's flyout: compact, rounded, translucent, and a thin bar with no thumb.
        "windows-11" => new Layout(
            Width: 300, VolumeHeight: 52, AirPlayHeight: 30,
            Track: new Box(52, 24, 186, 4),
            Glyph: new Box(16, 14, 24, 24),
            PercentX: 284, PercentY: 15, PercentAlign: "right", PercentSize: 15,
            AirTrack: new Box(52, 22, 174, 3),
            NameX: 52, NameY: 4, LevelX: 226, LevelY: 4, LabelSize: 11,
            ConnectHit: new Box(232, 3, 28, 24),
            DropdownHit: new Box(264, 3, 30, 24)),

        // Windows 10's flyout: square, opaque, and it leads with an icon block and a big number
        // rather than tucking the number away on the right.
        "windows-10" => new Layout(
            Width: 300, VolumeHeight: 68, AirPlayHeight: 34,
            Track: new Box(116, 32, 168, 4),
            Glyph: new Box(14, 22, 24, 24),
            PercentX: 68, PercentY: 18, PercentAlign: "left", PercentSize: 24,
            AirTrack: new Box(116, 26, 112, 4),
            NameX: 64, NameY: 6, LevelX: 228, LevelY: 6, LabelSize: 12,
            ConnectHit: new Box(234, 5, 28, 24),
            DropdownHit: new Box(266, 5, 28, 24)),

        // XP shipped no volume OSD, so the reference is its tray volume popup: a Luna panel with a
        // title, a trackbar in a sunken groove, and a Mute checkbox. Tall enough to hold all three.
        "windows-xp" => new Layout(
            Width: 300, VolumeHeight: 86, AirPlayHeight: 38,
            Track: new Box(48, 42, 204, 12),
            Glyph: new Box(14, 38, 22, 20),
            PercentX: 286, PercentY: 40, PercentAlign: "right", PercentSize: 13,
            AirTrack: new Box(48, 24, 176, 10),
            NameX: 12, NameY: 6, LevelX: 224, LevelY: 6, LabelSize: 11,
            ConnectHit: new Box(228, 6, 30, 26),
            DropdownHit: new Box(262, 6, 30, 26)),

        // The house style. Not a tribute to anything - it is what AorinEQ looks like, and it is
        // what "custom skin" resolves to before anybody picks one.
        _ => new Layout(
            Width: 300, VolumeHeight: 64, AirPlayHeight: 32,
            Track: new Box(52, 28, 184, 8),
            Glyph: new Box(16, 20, 24, 24),
            PercentX: 284, PercentY: 21, PercentAlign: "right", PercentSize: 17,
            AirTrack: new Box(52, 23, 170, 5),
            NameX: 52, NameY: 5, LevelX: 222, LevelY: 5, LabelSize: 12,
            ConnectHit: new Box(228, 4, 30, 24),
            DropdownHit: new Box(262, 4, 32, 24)),
    };

    private static Palette PaletteFor(string style) => style switch
    {
        // Mica-ish graphite at Windows 11's flyout opacity, its 8px flyout radius, and the Fluent
        // accent blue. Nothing here is rounder or softer than the real thing.
        "windows-11" => new Palette(
            Face: Color.FromArgb(0xF0, 0x2C, 0x2C, 0x2C),
            Edge: Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F),
            Track: Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A),
            Accent: Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF),
            Ink: Color.White, Corner: 8, EdgeWidth: 1f, Font: "Segoe UI"),

        // Flat, square, and OPAQUE - Windows 10's OSD did not blur what was behind it.
        "windows-10" => new Palette(
            Face: Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B),
            Edge: Color.FromArgb(0xFF, 0x45, 0x45, 0x45),
            Track: Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A),
            Accent: Color.FromArgb(0xFF, 0x00, 0x78, 0xD7),
            Ink: Color.White, Corner: 0, EdgeWidth: 1f, Font: "Segoe UI"),

        // Luna: the beige face, the blue frame, the navy ink, and the selection blue that fills
        // the trackbar. Tahoma because that is what XP set type in.
        "windows-xp" => new Palette(
            Face: Color.FromArgb(0xFF, 0xEC, 0xE9, 0xD8),
            Edge: Color.FromArgb(0xFF, 0x00, 0x54, 0xE3),
            Track: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Accent: Color.FromArgb(0xFF, 0x31, 0x6A, 0xC5),
            Ink: Color.FromArgb(0xFF, 0x0A, 0x24, 0x6A),
            Corner: 3, EdgeWidth: 2f, Font: "Tahoma"),

        // Graphite and steel, straight off the app icon, with a near-white fill that is
        // deliberately NOT any Windows accent - this one should not be mistaken for a tribute.
        _ => new Palette(
            Face: Color.FromArgb(0xF2, 0x1E, 0x22, 0x28),
            Edge: Color.FromArgb(0xFF, 0x56, 0x5D, 0x66),
            Track: Color.FromArgb(0xFF, 0x3A, 0x40, 0x49),
            Accent: Color.FromArgb(0xFF, 0xE8, 0xED, 0xF4),
            Ink: Color.FromArgb(0xFF, 0xF5, 0xF7, 0xFA),
            Corner: 14, EdgeWidth: 1f, Font: "Segoe UI"),
    };

    /// <summary>One layer of one style. Layer must be one of <see cref="Layers"/>; anything else
    /// throws, because a typo would otherwise ship a blank PNG.</summary>
    public static Bitmap Draw(string style, string layer)
    {
        var layout = LayoutFor(style);
        var palette = PaletteFor(style);

        bool airplay = layer.StartsWith("airplay", StringComparison.Ordinal);
        int height = airplay ? layout.AirPlayHeight : layout.VolumeHeight;

        var bmp = new Bitmap(layout.Width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        switch (layer)
        {
            case "empty": DrawVolume(g, style, layout, palette, lit: false, muted: false); break;
            case "full": DrawVolume(g, style, layout, palette, lit: true, muted: false); break;
            case "muted": DrawVolume(g, style, layout, palette, lit: false, muted: true); break;
            case "airplay-empty": DrawAirPlay(g, style, layout, palette, lit: false); break;
            case "airplay-full": DrawAirPlay(g, style, layout, palette, lit: true); break;
            default: throw new ArgumentException($"unknown layer '{layer}'", nameof(layer));
        }

        return bmp;
    }

    // ---- the volume bar -------------------------------------------------------------------

    /// <summary>One volume layer. All three paint the WHOLE image and differ only inside the track
    /// and on the speaker - the renderer swaps between them through a hole punched across the full
    /// height of the fill band, so anything one of them omits there is a transparent rectangle
    /// through the middle of the skin.</summary>
    private static void DrawVolume(
        Graphics g, string style, Layout l, Palette p, bool lit, bool muted)
    {
        var body = new Rectangle(0, 0, l.Width - 1, l.VolumeHeight - 1);

        if (style == "windows-xp")
        {
            DrawXpPanel(g, l, p, body, muted);
            DrawGroove(g, l.Track, p);
            if (lit) DrawTrackFill(g, l.Track, p.Accent, radius: 0);
            DrawXpTicks(g, l.Track, p);
            return;
        }

        DrawPanel(g, p, body);

        if (style == "windows-10")
        {
            // Windows 10 led with a square icon block the full height of the flyout. It is what
            // makes the thing recognisable from across the room, and it is why the number sits
            // beside it rather than out on the right.
            using var block = new SolidBrush(Color.FromArgb(0xFF, 0x3B, 0x3B, 0x3B));
            g.FillRectangle(block, 0, 0, 52, l.VolumeHeight);
            using var seam = new Pen(p.Edge);
            g.DrawLine(seam, 52, 0, 52, l.VolumeHeight);
        }

        DrawSpeaker(g, l.Glyph, p.Ink, muted);

        int radius = l.Track.H / 2;
        DrawTrack(g, l.Track, p.Track, radius);
        if (lit) DrawTrackFill(g, l.Track, p.Accent, radius);
    }

    /// <summary>The rounded (or square) body every non-XP style sits in.</summary>
    private static void DrawPanel(Graphics g, Palette p, Rectangle body)
    {
        using var path = Rounded(body, p.Corner);
        using var face = new SolidBrush(p.Face);
        using var edge = new Pen(p.Edge, p.EdgeWidth);
        g.FillPath(face, path);
        g.DrawPath(edge, path);
    }

    /// <summary>Luna: a blue frame, a beige face, a raised inner highlight, the word "Volume", and
    /// the Mute checkbox - which is TICKED on the muted layer, so the control says what is actually
    /// true rather than being decoration that never changes.</summary>
    private static void DrawXpPanel(Graphics g, Layout l, Palette p, Rectangle body, bool muted)
    {
        DrawPanel(g, p, body);

        // The 3D inner edge: light top-left, shadow bottom-right. The cheapest honest tell that
        // this is a 2001 control.
        using (var light = new Pen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)))
        using (var shade = new Pen(Color.FromArgb(0xFF, 0xAC, 0xA8, 0x99)))
        {
            g.DrawLine(light, 4, 4, body.Right - 4, 4);
            g.DrawLine(light, 4, 4, 4, body.Bottom - 4);
            g.DrawLine(shade, 4, body.Bottom - 4, body.Right - 4, body.Bottom - 4);
            g.DrawLine(shade, body.Right - 4, 4, body.Right - 4, body.Bottom - 4);
        }

        using var title = new Font(p.Font, 8.25f, FontStyle.Bold, GraphicsUnit.Point);
        using var label = new Font(p.Font, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
        using var ink = new SolidBrush(p.Ink);
        g.DrawString("Volume", title, ink, 12, 9);

        DrawSpeaker(g, l.Glyph, p.Ink, muted);

        var check = new Rectangle(14, l.VolumeHeight - 24, 13, 13);
        using (var white = new SolidBrush(Color.White))
        using (var frame = new Pen(Color.FromArgb(0xFF, 0x7F, 0x9D, 0xB9)))
        {
            g.FillRectangle(white, check);
            g.DrawRectangle(frame, check);
        }
        if (muted)
        {
            using var tick = new Pen(Color.FromArgb(0xFF, 0x21, 0x21, 0x21), 1.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(tick,
            [
                new PointF(check.X + 3, check.Y + 6.5f),
                new PointF(check.X + 5.5f, check.Y + 9.5f),
                new PointF(check.X + 10, check.Y + 3.5f),
            ]);
        }
        g.DrawString("Mute", label, ink, check.Right + 4, check.Y - 2);
    }

    /// <summary>XP's sunken trackbar groove: white interior, dark top-left, light bottom-right.</summary>
    private static void DrawGroove(Graphics g, Box track, Palette p)
    {
        var r = track.ToRect();
        using var interior = new SolidBrush(p.Track);
        using var dark = new Pen(Color.FromArgb(0xFF, 0x7F, 0x9D, 0xB9));
        using var light = new Pen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        g.FillRectangle(interior, r);
        g.DrawLine(dark, r.Left, r.Top, r.Right, r.Top);
        g.DrawLine(dark, r.Left, r.Top, r.Left, r.Bottom);
        g.DrawLine(light, r.Left, r.Bottom, r.Right, r.Bottom);
        g.DrawLine(light, r.Right, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The tick row under an XP trackbar. Drawn on every layer, outside the groove, so it
    /// is identical either side of the fill edge.</summary>
    private static void DrawXpTicks(Graphics g, Box track, Palette p)
    {
        using var pen = new Pen(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
        for (int i = 0; i <= 10; i++)
        {
            int x = track.X + (int)Math.Round(track.W * (i / 10.0));
            g.DrawLine(pen, x, track.Bottom + 3, x, track.Bottom + (i % 5 == 0 ? 7 : 5));
        }
    }

    private static void DrawTrack(Graphics g, Box track, Color color, int radius)
    {
        using var path = Rounded(track.ToRect(), radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>The accent inside the track - the only thing that distinguishes the full layer from
    /// the empty one. It fills the WHOLE track; the renderer's clip is what makes it a level.</summary>
    private static void DrawTrackFill(Graphics g, Box track, Color accent, int radius)
    {
        using var path = Rounded(track.ToRect(), radius);
        using var brush = new SolidBrush(accent);
        g.FillPath(brush, path);
    }

    // ---- the AirPlay strip ----------------------------------------------------------------

    /// <summary>One AirPlay layer: the same body as the volume bar in the same style, a slim
    /// underline rather than a full bar - the name and the level are the content, and a
    /// full-height bar under them would read as a second volume bar - and the chevron that says
    /// the strip opens something.</summary>
    private static void DrawAirPlay(Graphics g, string style, Layout l, Palette p, bool lit)
    {
        var body = new Rectangle(0, 0, l.Width - 1, l.AirPlayHeight - 1);

        if (style == "windows-xp")
        {
            DrawPanel(g, p, body);
            DrawGroove(g, l.AirTrack, p);
            if (lit) DrawTrackFill(g, l.AirTrack, p.Accent, radius: 0);
        }
        else
        {
            DrawPanel(g, p, body);
            if (style == "windows-10")
            {
                using var block = new SolidBrush(Color.FromArgb(0xFF, 0x3B, 0x3B, 0x3B));
                g.FillRectangle(block, 0, 0, 52, l.AirPlayHeight);
                using var seam = new Pen(p.Edge);
                g.DrawLine(seam, 52, 0, 52, l.AirPlayHeight);
            }

            int radius = l.AirTrack.H / 2;
            DrawTrack(g, l.AirTrack, p.Track, radius);
            if (lit) DrawTrackFill(g, l.AirTrack, p.Accent, radius);
        }

        DrawAirPlayGlyph(g, style, l, p);
        DrawChevron(g, l.DropdownHit, p.Ink);
    }

    /// <summary>The AirPlay mark - a triangle under an arc - where the volume bar puts its speaker,
    /// so the two rows read as a pair. Only Windows 10 has room for it beside its icon block; the
    /// others put the receiver's name there instead.</summary>
    private static void DrawAirPlayGlyph(Graphics g, string style, Layout l, Palette p)
    {
        if (style != "windows-10") return;

        using var pen = new Pen(p.Ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(p.Ink);
        float cx = 26, cy = l.AirPlayHeight / 2f;

        // The mark is a triangle under an arc, and the two have to be sized together or the arc
        // reads as a lone caret - which is what a squeezed first attempt looked like.
        g.DrawArc(pen, cx - 11, cy - 11, 22, 16, 195, 150);
        g.FillPolygon(brush,
        [
            new PointF(cx, cy - 1),
            new PointF(cx + 7, cy + 9),
            new PointF(cx - 7, cy + 9),
        ]);
    }

    /// <summary>The affordance that says the strip opens something. Drawn inside the box the skin
    /// declares as dropdownHit, so what the eye aims at and what the click test accepts are the
    /// same rectangle.</summary>
    private static void DrawChevron(Graphics g, Box hit, Color ink)
    {
        float cx = hit.CenterX;
        float cy = hit.CenterY;

        using var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen,
        [
            new PointF(cx - 4, cy - 2),
            new PointF(cx, cy + 2),
            new PointF(cx + 4, cy - 2),
        ]);
    }

    // ---- shared pieces --------------------------------------------------------------------

    /// <summary>A speaker: the cone, and either two waves or the slash that means muted. Drawn as
    /// geometry rather than set in Segoe Fluent Icons because these PNGs are rendered on the user's
    /// machine at first run, and a skin must not come out blank on one that lacks the font.</summary>
    private static void DrawSpeaker(Graphics g, Box box, Color ink, bool muted)
    {
        float x = box.X;
        float y = box.Y;
        float s = box.H / 24f; // the geometry below is authored on a 24px grid

        using var brush = new SolidBrush(ink);
        using var pen = new Pen(ink, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Cone: the square body plus the flare out to the right.
        g.FillPolygon(brush,
        [
            new PointF(x + 2 * s, y + 9 * s),
            new PointF(x + 6 * s, y + 9 * s),
            new PointF(x + 11 * s, y + 4 * s),
            new PointF(x + 11 * s, y + 20 * s),
            new PointF(x + 6 * s, y + 15 * s),
            new PointF(x + 2 * s, y + 15 * s),
        ]);

        if (muted)
        {
            g.DrawLine(pen, x + 14 * s, y + 8 * s, x + 21 * s, y + 16 * s);
            g.DrawLine(pen, x + 21 * s, y + 8 * s, x + 14 * s, y + 16 * s);
            return;
        }

        g.DrawArc(pen, x + 11 * s, y + 8 * s, 6 * s, 8 * s, -60, 120);
        g.DrawArc(pen, x + 12 * s, y + 4 * s, 10 * s, 16 * s, -55, 110);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The skin.json each style ships, describing the artwork above: where the bar fills,
    /// where the numbers sit, and where the AirPlay strip is clicked.
    ///
    /// Generated from the same <see cref="Layout"/> the drawing reads rather than written out four
    /// times, so a fill range changed in the drawing cannot silently disagree with the JSON that
    /// describes it.</summary>
    public static string SkinJson(string style)
    {
        var l = LayoutFor(style);
        var p = PaletteFor(style);
        string ink = $"#FF{p.Ink.R:X2}{p.Ink.G:X2}{p.Ink.B:X2}";
        var hit = l.DropdownHit;
        var power = l.ConnectHit;

        return $$"""
        {
          "title": "{{Title(style)}}",
          "author": "AorinEQ",
          "description": "{{Description(style)}}",
          "percentText": {
            "show": true, "x": {{l.PercentX}}, "y": {{l.PercentY}},
            "align": "{{l.PercentAlign}}", "color": "{{ink}}", "fontFamily": "{{p.Font}}",
            "fontSize": {{l.PercentSize}}
          },
          "fillStartX": {{l.Track.X}},
          "fillEndX": {{l.Track.Right}},
          "airplay": {
            "fillStartX": {{l.AirTrack.X}},
            "fillEndX": {{l.AirTrack.Right}},
            "nameText": {
              "show": true, "x": {{l.NameX}}, "y": {{l.NameY}},
              "align": "left", "color": "{{ink}}", "fontFamily": "{{p.Font}}",
              "fontSize": {{l.LabelSize}}
            },
            "percentText": {
              "show": true, "x": {{l.LevelX}}, "y": {{l.LevelY}},
              "align": "right", "color": "{{ink}}", "fontFamily": "{{p.Font}}",
              "fontSize": {{l.LabelSize}}
            },
            "connectHit": { "x": {{power.X}}, "y": {{power.Y}}, "w": {{power.W}}, "h": {{power.H}} },
            "connectColor": "{{Lit(style)}}",
            "dropdownHit": { "x": {{hit.X}}, "y": {{hit.Y}}, "w": {{hit.W}}, "h": {{hit.H}} }
          }
        }
        """;
    }

    /// <summary>What the power button turns while a session is live. The dim state is the name's
    /// own colour, so only this one has to be said - and it is said per style because a Windows
    /// accent glowing on a Luna panel would look like a rendering fault rather than a state.</summary>
    private static string Lit(string style) => style switch
    {
        "windows-11" => "#FF4CC2FF",
        "windows-10" => "#FF0078D7",
        // XP had no accent to borrow, so this is the Luna progress green - the colour that meant
        // "something is happening" on that desktop.
        "windows-xp" => "#FF2CC12C",
        _ => "#FF7FD3FF",
    };

    private static string Title(string style) => style switch
    {
        "windows-11" => "Windows 11",
        "windows-10" => "Windows 10",
        "windows-xp" => "Windows XP",
        _ => "AorinEQ",
    };

    private static string Description(string style) => style switch
    {
        "windows-11" => "The Windows 11 volume flyout: rounded, translucent graphite and the Fluent accent.",
        "windows-10" => "The Windows 10 volume flyout: square and opaque, an icon block and a big number.",
        "windows-xp" => "XP had no volume OSD, so this is its tray volume popup: Luna, a trackbar and a Mute box.",
        _ => "AorinEQ's own: graphite and steel, and the skin you get before you pick one.",
    };
}
