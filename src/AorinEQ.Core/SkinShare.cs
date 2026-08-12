namespace AorinEQ.Core;

/// <summary>Builds the <c>aorineq://install-skin</c> link that goes on the clipboard beside an
/// exported skin zip.
///
/// It is a TEMPLATE, not a finished link: the zip has to be hosted somewhere over https before
/// any URL can point at it, and the author is the only one who knows where that will be. The
/// placeholder host makes the shape obvious and keeps the link parseable, so pasting it into the
/// gallery's link generator (which fills in the real URL and computes the sha256 pin) works
/// without anyone having to remember the query-string contract.
///
/// No <c>sha256</c> is emitted on purpose. The digest pins one exact file at one exact URL; until
/// the zip is actually uploaded there is no such file, and a pin computed from the local copy
/// would go stale the moment the author re-exported — turning every already-shared link into a
/// download that refuses to install.</summary>
public static class SkinShare
{
    /// <summary>The stand-in host in the template's URL. Deliberately example.com: it is reserved
    /// for exactly this, so nobody's real site is named and nothing resolves by accident.</summary>
    public const string PlaceholderHost = "example.com";

    /// <summary>What the author still has to do, in one line, shown next to the Share action and
    /// repeated in its status message.</summary>
    public const string HostingHint =
        "Upload the zip somewhere it can be downloaded over https, put that address in the link "
        + "where the example one is, and the gallery's link generator will add the sha256 that "
        + "pins it.";

    /// <summary>The install-link template for a skin, ready to be pasted and edited.</summary>
    /// <exception cref="ArgumentException">The name is not a valid skin name, so the link could
    /// never install anything.</exception>
    public static string BuildInstallLinkTemplate(string skinName)
    {
        if (SkinWriter.ValidateName(skinName) is { } error)
            throw new ArgumentException(error, nameof(skinName));

        var name = skinName.Trim();
        // Both values are percent-encoded: a skin name may contain spaces, '&' or '#', all of
        // which would otherwise end the value early — and a link carrying whitespace is rejected
        // by ProtocolLink.IsSafeToForward when the shell hands it to an elevated instance.
        var url = Uri.EscapeDataString($"https://{PlaceholderHost}/skins/{name}.zip");
        return $"{ProtocolLink.Scheme}://{ProtocolLink.InstallSkinAction}"
            + $"?url={url}&name={Uri.EscapeDataString(name)}";
    }
}
