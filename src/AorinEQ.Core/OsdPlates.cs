namespace AorinEQ.Core;

/// <summary>
/// The built-in OSD plate looks, in one place. A skin supplies its own <see cref="SkinNotice"/>;
/// every other style resolves here. Style names are the <c>Settings.OsdStyle</c> values
/// (the app's <c>OsdStyles</c> constants live in the UI project, which Core cannot reference).
/// </summary>
public static class OsdPlates
{
    public static SkinNotice DarkPill { get; } = SkinLoader.DefaultNotice;

    public static SkinNotice MinimalBar { get; } = DarkPill with
    {
        CornerRadius = 8, PadX = 10, PadY = 4,
        Text = DarkPill.Text with { FontSize = 12 },
        Glyph = DarkPill.Glyph with { Size = 14 },
    };

    public static SkinNotice FluentLight { get; } = DarkPill with
    {
        Background = "#F2F3F3F3", Border = "#14000000", CornerRadius = 8,
        Text = DarkPill.Text with { Color = "#FF000000" },
    };

    public static SkinNotice FluentDark { get; } = DarkPill with
    {
        Border = "#26FFFFFF", CornerRadius = 8,
    };

    public static SkinNotice NoticeFor(string osdStyle, bool light, SkinNotice? skinNotice) => osdStyle switch
    {
        "skin" => skinNotice ?? DarkPill,
        "fluent" => light ? FluentLight : FluentDark,
        "minimal-bar" => MinimalBar,
        _ => DarkPill,
    };
}
