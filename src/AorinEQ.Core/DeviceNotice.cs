namespace AorinEQ.Core;

/// <summary>Decides whether a change of the Windows default playback device earns an on-screen
/// notice, and what the notice says.
///
/// It exists because a USB DAC that drops out is replaced SILENTLY: Windows moves the default to
/// whatever is left and says nothing, and the first the user knows of it is sound coming out of
/// the wrong thing. The window that draws the notice knows none of this - it is handed a string.
///
/// One switch is one notice. <see cref="EndpointVolume"/> already listens for the Multimedia role
/// alone, so Windows announcing a switch once per role arrives here once; what is left to collapse
/// is a notification that lands on the device the app is already on, which is what a rapid
/// A -> B -> A leaves behind (every queued notification re-reads the CURRENT default). Comparing
/// endpoint ids covers both, and is why this keeps the id rather than trusting the event count.
///
/// Not thread-safe by design - all access happens on the dispatcher thread, like
/// <see cref="DeviceVolumeStates"/>, whose active device this follows.</summary>
public sealed class DeviceNotice
{
    /// <summary>The longest device name shown whole. Windows names endpoints after their driver
    /// ("Headphones (WH-1000XM4 Hands-Free AG Audio)" is 43), and a notice has one line.</summary>
    public const int MaxNameLength = 48;

    private string? _deviceId;

    /// <param name="initialDeviceId">The device the app starts on, or null when there is none.
    /// Taken here rather than through <see cref="Next"/> so that starting up is not a switch.</param>
    public DeviceNotice(string? initialDeviceId) => _deviceId = initialDeviceId;

    /// <summary>The text to show for the device the app has just moved to, or null for nothing.
    ///
    /// The device is remembered even while <paramref name="enabled"/> is false, so turning the
    /// setting on later does not announce a switch that happened an hour ago.</summary>
    /// <param name="deviceId">The new default endpoint; null when Windows has none.</param>
    /// <param name="deviceName">Its friendly name; empty when it could not be read.</param>
    /// <param name="noDeviceText">Shown when everything has been unplugged.</param>
    /// <param name="unnamedDeviceText">Shown for a device whose name could not be read - the
    /// switch still happened, and saying nothing would hide exactly what this is for.</param>
    public string? Next(bool enabled, string? deviceId, string? deviceName,
        string noDeviceText, string unnamedDeviceText)
    {
        if (deviceId == _deviceId) return null;
        _deviceId = deviceId;
        if (!enabled) return null;
        if (deviceId is null) return noDeviceText;
        return string.IsNullOrWhiteSpace(deviceName) ? unnamedDeviceText : Truncate(deviceName.Trim());
    }

    /// <summary>Cuts a name to <see cref="MaxNameLength"/> characters, the last of them an
    /// ellipsis. Never splits a surrogate pair: half of one renders as a replacement box.</summary>
    public static string Truncate(string name)
    {
        if (name.Length <= MaxNameLength) return name;
        int keep = MaxNameLength - 1;
        if (char.IsHighSurrogate(name[keep - 1])) keep--;
        return name[..keep].TrimEnd() + "…";
    }
}
