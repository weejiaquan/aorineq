namespace AorinEQ.UI;

/// <summary>
/// OSD style identifiers. <see cref="DarkPill"/>, <see cref="MinimalBar"/>, and <see
/// cref="Fluent"/> are rendered by <see cref="OsdWindow"/> swapping its own visual tree; <see
/// cref="Skin"/> is rendered by a separate <see cref="SkinOsdWindow"/> instance instead (see
/// <c>App</c>'s style-switching logic). <see cref="OsdWindow"/> falls back to <see
/// cref="DarkPill"/> for any style value other than <see cref="MinimalBar"/> and <see
/// cref="Fluent"/> (including <see cref="Skin"/>, and any corrupted setting), so it always
/// renders something reasonable even while it isn't the active window.
/// </summary>
public static class OsdStyles
{
    public const string DarkPill = "dark-pill";
    public const string MinimalBar = "minimal-bar";
    public const string Fluent = "fluent";
    public const string Skin = "skin";
}
