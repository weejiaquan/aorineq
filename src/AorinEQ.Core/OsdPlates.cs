namespace AorinEQ.Core;

/// <summary>
/// The built-in OSD plate looks, in one place. A skin supplies its own <see cref="SkinNotice"/>;
/// every other style resolves here. Style names are the <see cref="OsdStyles"/> values.
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
        OsdStyles.Skin => skinNotice ?? DarkPill,
        OsdStyles.Fluent => light ? FluentLight : FluentDark,
        OsdStyles.MinimalBar => MinimalBar,
        _ => DarkPill,
    };
}
