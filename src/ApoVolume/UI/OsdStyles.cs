namespace ApoVolume.UI;

/// <summary>
/// OSD style identifiers that <see cref="OsdWindow"/> renders directly by swapping its own
/// visual tree. <c>Settings.OsdStyle</c> also allows "fluent" and "skin" (see
/// <c>ApoVolume.Core.Settings</c>'s valid-styles list) — those belong to other, not-yet-built
/// rendering paths (a Fluent-styled tree and the custom SkinLoader-driven skin renderer,
/// respectively). Until those exist, <see cref="OsdWindow"/> falls back to
/// <see cref="DarkPill"/> for any style value other than <see cref="MinimalBar"/>, so an
/// unimplemented or corrupted style setting never leaves the OSD blank.
/// </summary>
public static class OsdStyles
{
    public const string DarkPill = "dark-pill";
    public const string MinimalBar = "minimal-bar";
}
