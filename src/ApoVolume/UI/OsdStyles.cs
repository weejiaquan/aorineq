namespace ApoVolume.UI;

/// <summary>
/// OSD style identifiers that <see cref="OsdWindow"/> renders directly by swapping its own
/// visual tree. <c>Settings.OsdStyle</c> also allows "skin" (see
/// <c>ApoVolume.Core.Settings</c>'s valid-styles list) — that belongs to another, not-yet-built
/// rendering path (the custom SkinLoader-driven skin renderer). Until that exists, <see
/// cref="OsdWindow"/> falls back to <see cref="DarkPill"/> for any style value other than <see
/// cref="MinimalBar"/> and <see cref="Fluent"/>, so an unimplemented or corrupted style setting
/// never leaves the OSD blank.
/// </summary>
public static class OsdStyles
{
    public const string DarkPill = "dark-pill";
    public const string MinimalBar = "minimal-bar";
    public const string Fluent = "fluent";
}
