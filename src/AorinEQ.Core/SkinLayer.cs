namespace AorinEQ.Core;

/// <summary>Every node of the Skin Designer's Layers tree, and so every part of a skin a user can
/// select: the skin's own details, the volume bar with its three art layers and its number, the
/// AirPlay strip and its parts, and the device notice and its parts.</summary>
public enum SkinLayer
{
    SkinInfo,
    VolumeBar, VolumeEmpty, VolumeFull, VolumeMuted, PercentText,
    AirPlay, AirPlayEmpty, AirPlayFull, AirPlayName, AirPlayLevel, AirPlayDropdown, AirPlayPower,
    Notice, NoticeArt, NoticeText, NoticeGlyph,
}
