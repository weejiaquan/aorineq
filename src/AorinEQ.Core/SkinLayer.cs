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

/// <summary>The part of a canvas element a drag holds: the element itself, one end of a fill
/// range, the right edge of the notice text box, or a corner of a rectangle.</summary>
public enum SkinHandle { Move, FillStart, FillEnd, Right, TopLeft, TopRight, BottomLeft, BottomRight }

public static class SkinLayers
{
    /// <summary>The artboard a layer is drawn on - the volume bar, the AirPlay strip or the notice.
    /// Skin info is on none and answers itself.</summary>
    public static SkinLayer BoardOf(SkinLayer layer) => layer switch
    {
        SkinLayer.VolumeBar or SkinLayer.VolumeEmpty or SkinLayer.VolumeFull or SkinLayer.VolumeMuted
            or SkinLayer.PercentText => SkinLayer.VolumeBar,
        SkinLayer.AirPlay or SkinLayer.AirPlayEmpty or SkinLayer.AirPlayFull or SkinLayer.AirPlayName
            or SkinLayer.AirPlayLevel or SkinLayer.AirPlayDropdown or SkinLayer.AirPlayPower => SkinLayer.AirPlay,
        SkinLayer.Notice or SkinLayer.NoticeArt or SkinLayer.NoticeText or SkinLayer.NoticeGlyph => SkinLayer.Notice,
        _ => SkinLayer.SkinInfo,
    };
}
