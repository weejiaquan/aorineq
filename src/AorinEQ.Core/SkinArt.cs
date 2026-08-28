using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AorinEQ.Core;

/// <summary>The artwork for the three skins AorinEQ ships with: Windows 11, Windows 10 and a
/// Windows XP throwback.
///
/// DRAWN, not authored in an image editor, for the same reason <see cref="AppIconArt"/> is: art
/// that lives as code can be regenerated, reviewed in a diff and tested. It also means the shipped
/// skins cost the installer nothing - <see cref="DefaultSkins"/> renders them into the user's
/// skins folder on first run, so a single-file exe carries no image payload at all.
///
/// Each style draws FOUR layers. empty/full are the volume bar every skin has had since 1.0.
/// airplay-empty/airplay-full are the strip added alongside the OSD's AirPlay control - and they
/// are the reason these three exist as more than decoration: they are the reference for what an
/// AirPlay bar is supposed to look like, in a format a skin author can copy.
///
/// The layer contract they follow is the one the loader documents: empty.png carries ALL the
/// static decoration, and full.png paints only the lit pixels inside the fill range, so the clip
/// between fillStartX and fillEndX is pixel-exact at both ends.</summary>
public static class SkinArt
{
    public const int Width = 300;
    public const int VolumeHeight = 64;
    public const int AirPlayHeight = 32;

    /// <summary>Where the bar's lit pixels start and stop, in both strips. The margin either side
    /// is decoration, which is exactly what fillStartX/fillEndX exist to express.</summary>
    public const int FillStartX = 16;
    public const int FillEndX = 248;

    /// <summary>The chevron's box on the AirPlay strip - what a skin declares as dropdownHit so a
    /// click there opens the device list instead of setting a volume.</summary>
    public static readonly (int X, int Y, int W, int H) DropdownHit = (258, 4, 30, 24);

    public static readonly IReadOnlyList<string> All = ["windows-11", "windows-10", "windows-xp"];

    /// <summary>One layer of one style. Layer is "empty", "full", "airplay-empty" or
    /// "airplay-full"; anything else throws, because a typo would otherwise ship a blank PNG.</summary>
    public static Bitmap Draw(string style, string layer)
    {
        int height = layer.StartsWith("airplay", StringComparison.Ordinal) ? AirPlayHeight : VolumeHeight;
        var bmp = new Bitmap(Width, height);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        var palette = PaletteFor(style);
        bool airplay = height == AirPlayHeight;
        bool full = layer.EndsWith("full", StringComparison.Ordinal);

        if (full) DrawFull(g, palette, airplay);
        else DrawEmpty(g, palette, airplay);

        return bmp;
    }

    /// <summary>The one place a style's colours are written down.</summary>
    private sealed record Palette(
        Color Face, Color Edge, Color Track, Color Accent, Color Ink, int Corner, bool Bevel,
        string Font);

    private static Palette PaletteFor(string style) => style switch
    {
        // Mica-ish graphite, a generous radius and the Fluent accent blue.
        "windows-11" => new Palette(
            Face: Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B),
            Edge: Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D),
            Track: Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A),
            Accent: Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF),
            Ink: Color.White, Corner: 12, Bevel: false, Font: "Segoe UI"),

        // Flat, square, and the older accent. Windows 10 had no rounding and did not pretend to.
        "windows-10" => new Palette(
            Face: Color.FromArgb(0xF7, 0x1F, 0x1F, 0x1F),
            Edge: Color.FromArgb(0xFF, 0x45, 0x45, 0x45),
            Track: Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A),
            Accent: Color.FromArgb(0xFF, 0x00, 0x78, 0xD7),
            Ink: Color.White, Corner: 0, Bevel: false, Font: "Segoe UI"),

        // Luna: the beige face, the blue frame, and a green progress bar that fills in blocks.
        _ => new Palette(
            Face: Color.FromArgb(0xFF, 0xEC, 0xE9, 0xD8),
            Edge: Color.FromArgb(0xFF, 0x00, 0x54, 0xE3),
            Track: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Accent: Color.FromArgb(0xFF, 0x2C, 0xC1, 0x2C),
            Ink: Color.FromArgb(0xFF, 0x0A, 0x24, 0x6A), Corner: 3, Bevel: true, Font: "Tahoma"),
    };

    private static void DrawEmpty(Graphics g, Palette p, bool airplay)
    {
        int h = airplay ? AirPlayHeight : VolumeHeight;
        var body = new Rectangle(0, 0, Width - 1, h - 1);

        using (var path = Rounded(body, p.Corner))
        using (var face = new SolidBrush(p.Face))
        using (var edge = new Pen(p.Edge, p.Bevel ? 2f : 1f))
        {
            g.FillPath(face, path);
            g.DrawPath(edge, path);
        }

        // XP's sunken inner line: the cheapest honest tell that this is a 2001 control.
        if (p.Bevel)
        {
            using var shade = new Pen(Color.FromArgb(0x60, 0x80, 0x80, 0x80));
            g.DrawRectangle(shade, 3, 3, Width - 7, h - 7);
        }

        var track = TrackRect(airplay);
        using (var path = Rounded(track, p.Bevel ? 0 : 3))
        using (var brush = new SolidBrush(p.Track))
        {
            g.FillPath(brush, path);
            if (p.Bevel)
            {
                using var groove = new Pen(Color.FromArgb(0xFF, 0x7F, 0x9D, 0xB9));
                g.DrawPath(groove, path);
            }
        }

        if (airplay) DrawChevron(g, p);
    }

    /// <summary>The lit layer: ONLY the pixels inside the fill range. Everything static belongs in
    /// the empty layer, which is what makes the clip exact at 0% and at 100%.</summary>
    private static void DrawFull(Graphics g, Palette p, bool airplay)
    {
        var track = TrackRect(airplay);

        if (p.Bevel)
        {
            // XP filled its progress bars in discrete blocks. The clip reveals whole blocks as the
            // level rises, which is the effect without needing any animation.
            using var brush = new SolidBrush(p.Accent);
            for (int x = track.X + 1; x + 6 <= track.Right - 1; x += 8)
                g.FillRectangle(brush, x, track.Y + 1, 6, track.Height - 2);
            return;
        }

        using (var path = Rounded(track, 3))
        using (var brush = new SolidBrush(p.Accent))
        {
            g.FillPath(brush, path);
        }
    }

    private static Rectangle TrackRect(bool airplay) => airplay
        // A slim underline on the AirPlay strip: the name and the level are the content, and a
        // full-height bar under them would read as the volume bar rather than as its companion.
        ? new Rectangle(FillStartX, AirPlayHeight - 9, FillEndX - FillStartX, 5)
        : new Rectangle(FillStartX, VolumeHeight / 2 - 4, FillEndX - FillStartX, 8);

    /// <summary>The affordance that says the strip opens something. Drawn into the EMPTY layer,
    /// inside the box the skin declares as dropdownHit, so what the eye aims at and what the click
    /// test accepts are the same rectangle.</summary>
    private static void DrawChevron(Graphics g, Palette p)
    {
        var (hx, hy, hw, hh) = DropdownHit;
        float cx = hx + hw / 2f;
        float cy = hy + hh / 2f;

        using var pen = new Pen(p.Ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - 4, cy - 2),
            new PointF(cx, cy + 2),
            new PointF(cx + 4, cy - 2),
        });
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
    /// Written here rather than as three literal files so the geometry has ONE source - a fill
    /// range changed in the drawing above cannot silently disagree with the JSON that describes
    /// it.</summary>
    public static string SkinJson(string style)
    {
        var p = PaletteFor(style);
        string ink = $"#FF{p.Ink.R:X2}{p.Ink.G:X2}{p.Ink.B:X2}";
        var (hx, hy, hw, hh) = DropdownHit;

        return $$"""
        {
          "title": "{{Title(style)}}",
          "author": "AorinEQ",
          "description": "{{Description(style)}}",
          "percentText": {
            "show": true, "x": {{FillEndX + 44}}, "y": {{VolumeHeight / 2 - 11}},
            "align": "right", "color": "{{ink}}", "fontFamily": "{{p.Font}}", "fontSize": 16
          },
          "fillStartX": {{FillStartX}},
          "fillEndX": {{FillEndX}},
          "airplay": {
            "fillStartX": {{FillStartX}},
            "fillEndX": {{FillEndX}},
            "nameText": {
              "show": true, "x": {{FillStartX}}, "y": 5,
              "align": "left", "color": "{{ink}}", "fontFamily": "{{p.Font}}", "fontSize": 12
            },
            "percentText": {
              "show": true, "x": {{FillEndX}}, "y": 5,
              "align": "right", "color": "{{ink}}", "fontFamily": "{{p.Font}}", "fontSize": 12
            },
            "dropdownHit": { "x": {{hx}}, "y": {{hy}}, "w": {{hw}}, "h": {{hh}} }
          }
        }
        """;
    }

    private static string Title(string style) => style switch
    {
        "windows-11" => "Windows 11",
        "windows-10" => "Windows 10",
        _ => "Windows XP",
    };

    private static string Description(string style) => style switch
    {
        "windows-11" => "Rounded graphite and the Fluent accent, like the Windows 11 volume flyout.",
        "windows-10" => "Flat, square and unapologetically 2015.",
        _ => "Luna. Beige face, blue frame, and a progress bar that fills in blocks.",
    };
}
