namespace ApoVolume.UI;

/// <summary>
/// OSD-only settings snapshot raised by <see cref="SettingsWindow"/>'s <c>OsdSettingsChanged</c>
/// event whenever any OSD control changes. Deliberately excludes Percent/Muted/RunAsAdmin — those
/// are owned by <c>App</c>'s <see cref="ApoVolume.Core.VolumeState"/> and RunAsAdmin toggle, not by
/// this window — so <c>App</c> merges just these fields into its persisted <see
/// cref="ApoVolume.Core.Settings"/> via a <c>with</c> expression rather than replacing it wholesale.
/// That merge is what keeps a volume change landing while the Settings window is open from being
/// clobbered by a stale snapshot (the same class of bug this task's settings-persistence fix
/// addresses for RunAsAdmin/volume changes).
/// </summary>
public sealed record OsdSettings(
    string Style, string SkinName, string Anchor, int OffsetX, int OffsetY,
    double HideDelaySeconds, bool AnimationEnabled, int AnimationMs, int StepPercent);
