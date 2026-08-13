namespace AorinEQ.Core;

/// <summary>The Settings window's sidebar sections, spelled once.
///
/// Each name is a contract between three things that must agree: the NavigationView item in the
/// XAML, the content page it selects, and the <c>aorineq://open?page=</c> deep links that land on
/// one. Keeping the list and the routing here — rather than as string literals spread across the
/// XAML and <c>App</c> — is what makes the routing testable at all, and it is the same idiom
/// <see cref="VolumeModes"/> and <see cref="ProtocolPages"/> already use for their own vocabularies.</summary>
public static class SettingsSections
{
    public const string Volume = "volume";
    public const string Osd = "osd";
    public const string Skins = "skins";
    public const string Equalizer = "equalizer";
    public const string Hud = "hud";
    public const string Updates = "updates";
    public const string About = "about";

    /// <summary>Every section, in sidebar order. The FIRST entry is also the default landing
    /// section — see <see cref="ForProtocolPage"/>.</summary>
    public static readonly IReadOnlyList<string> All =
        [Volume, Osd, Skins, Equalizer, Hud, Updates, About];

    public static bool IsSection(string section) => All.Contains(section);

    /// <summary>The section an <c>aorineq://open?page=</c> link should land on. Only
    /// <see cref="ProtocolPages.Skins"/> names a section directly; <c>eq</c> and <c>designer</c>
    /// open windows of their own and never reach here. Anything else — including the bare
    /// <c>settings</c> page and any page this build does not know — opens the FIRST section, which
    /// mirrors how the app already treats an unrecognised page ("just open Settings") instead of
    /// throwing or showing a blank shell.</summary>
    public static string ForProtocolPage(string page) =>
        page == ProtocolPages.Skins ? Skins : All[0];
}
