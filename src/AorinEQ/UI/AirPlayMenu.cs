using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
using AorinEQ.Core.Raop;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace AorinEQ.UI;

/// <summary>The device list the AirPlay bar drops down: every receiver on the network, the one
/// that is ours ticked, and the way to connect, disconnect or look again.
///
/// A native menu, deliberately, even under a heavily skinned OSD. A dropdown is a MENU - the tray
/// already shows the same list as one - and menus staying native is normal in skinned apps. It
/// also keeps the variable-length part of this feature out of the skin format: a skin declares
/// where the bar is clicked, and never has to lay out a list that might hold none or twelve.
///
/// Built fresh on every open rather than kept and mutated: discovery is a live thing, and a menu
/// built once would quietly show a receiver that left the network ten minutes ago.</summary>
public static class AirPlayMenu
{
    /// <summary>Opens the menu against <paramref name="target"/>.</summary>
    /// <param name="devices">What discovery last found. Empty is normal and is said out loud.</param>
    /// <param name="currentId">The receiver this app holds a session with, if any.</param>
    /// <param name="chosenId">The receiver SETTINGS name, which is not the same thing: a device
    /// can be chosen and not connected - after a restart, or once it has been disconnected - and
    /// that is exactly when an explicit Connect is worth having.</param>
    /// <param name="closed">Raised once the menu goes away, whatever closed it. The OSD hides
    /// itself on a timer that pauses while the pointer is over it - and the pointer is over the
    /// MENU, which is a different window - so it needs telling when to start counting again.</param>
    public static void Show(
        FrameworkElement target,
        IReadOnlyList<AirPlayDevice> devices,
        string? currentId,
        string? chosenId,
        Action<AirPlayDevice> onConnect,
        Action onDisconnect,
        Action onRescan,
        Action? closed = null)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        if (devices.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Loc.T("osd.airplay.none-found"),
                IsEnabled = false,
            });
        }

        foreach (var device in devices)
        {
            bool isCurrent = currentId is not null && device.Id == currentId;
            var item = new MenuItem
            {
                Header = device.DisplayName,
                IsCheckable = true,
                IsChecked = isCurrent,
            };

            // Captured per iteration, so the handler connects to the device on ITS row rather than
            // to whichever one the loop happened to end on.
            var captured = device;
            item.Click += (_, _) =>
            {
                if (isCurrent) onDisconnect();
                else onConnect(captured);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        if (currentId is not null)
        {
            var disconnect = new MenuItem { Header = Loc.T("osd.airplay.disconnect") };
            disconnect.Click += (_, _) => onDisconnect();
            menu.Items.Add(disconnect);
        }
        else if (chosenId is not null
                 && devices.FirstOrDefault(d => d.Id == chosenId) is { } chosen)
        {
            // Chosen but not connected. Clicking its row would do this too, but the row is one of
            // possibly a dozen and this is the one the user already picked - it should not need
            // finding again.
            var connect = new MenuItem { Header = Loc.T("osd.airplay.connect") };
            connect.Click += (_, _) => onConnect(chosen);
            menu.Items.Add(connect);
        }

        var rescan = new MenuItem { Header = Loc.T("osd.airplay.rescan") };
        rescan.Click += (_, _) => onRescan();
        menu.Items.Add(rescan);

        if (closed is not null) menu.Closed += (_, _) => closed();

        menu.IsOpen = true;
    }
}
