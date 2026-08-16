namespace AorinEQ.UI;

/// <summary>Tray-only settings snapshot raised by <see cref="SettingsWindow"/>'s
/// <c>TrayBehaviourChanged</c> event whenever one of the tray controls changes. Same contract as
/// <see cref="OsdSettings"/> and for the same reason: <c>App</c> merges just these fields into its
/// persisted <see cref="AorinEQ.Core.Settings"/> with a <c>with</c> expression, so a volume change
/// landing while the Settings window is open is never clobbered by a stale snapshot.
///
/// <paramref name="LeftClick"/> and <paramref name="MiddleClick"/> are
/// <see cref="AorinEQ.Core.TrayActions"/> names.</summary>
public sealed record TrayBehaviour(
    string LeftClick, string MiddleClick, bool ScrollEnabled, bool ScrollInverted);
