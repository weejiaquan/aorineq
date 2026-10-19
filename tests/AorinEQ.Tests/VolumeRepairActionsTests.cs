using System.Windows;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The Volume page's Repair and Undo, on the real Settings window, while AirPlay only holds
/// Windows' default on VB-Cable. Then the health reading is of the cable, and those buttons would act
/// on the cable (the default device) behind the AirPlay page's own "EQ the AirPlay stream" switch, so
/// the page hides them — the banner's repair button, the card's Repair, and Undo even with a backup —
/// and gives each back exactly as it was once the default is the user's again. Whether the default is
/// held there is App's <see cref="DefaultEndpointSwitcher.IsHeldOnCable"/> (tested in Core); this is
/// the window's half.</summary>
[Collection("Wpf")]
public class VolumeRepairActionsTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public VolumeRepairActionsTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>A reading that puts the banner up with "Repair automatically" on offer: Equalizer APO
    /// installed but not on the device. The endpoint guid is made up, so no real backup or
    /// interrupted repair on this machine can change what the window offers.</summary>
    private static readonly EapoHealthSnapshot Unhealthy = new(Installed: true, ActiveOnDevice: false,
        IncludeLinePresent: true, EndpointGuid: "{deadbeef-0000-0000-0000-00000000c0de}", DateTimeOffset.Now);

    private sealed record Actions(Visibility Banner, Visibility BannerRepair, Visibility Repair, Visibility Undo);

    private static Actions Read(SettingsWindow w) =>
        new(w.EapoBanner.Visibility, w.EapoBannerRepairButton.Visibility, w.RepairEapoButton.Visibility,
            w.UndoRepairButton.Visibility);

    private T With<T>(Func<SettingsWindow, T> body) =>
        _sta.Run(() =>
        {
            // Resolved from App.xaml in the app; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var window = new SettingsWindow(autostartEnabled: false, runAsAdmin: false, isElevated: false,
                version: "0.0.0", Settings.Default, health: null);
            try
            {
                window.SetEapoHealth(Unhealthy, VolumeModes.Eapo, applies: true);
                window.SetEapoUndoAvailable(true);
                return body(window);
            }
            finally { window.Hide(); }
        });

    [Fact]
    public void Held_on_the_cable_the_banner_stays_but_repair_and_undo_are_hidden_until_the_default_is_back()
    {
        var (normal, held, released) = With(w =>
        {
            var normal = Read(w);
            w.SetEapoHeldOnCable(true);
            var held = Read(w);
            w.SetEapoHeldOnCable(false);
            return (normal, held, Read(w));
        });
        _out.WriteLine($"not held: {normal}");
        _out.WriteLine($"held on the cable: {held}");
        _out.WriteLine($"released: {released}");

        Assert.Equal(new Actions(Visibility.Visible, Visibility.Visible, Visibility.Visible, Visibility.Visible), normal);
        // The truth is still shown; only the actions aimed at the cable go.
        Assert.Equal(new Actions(Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed), held);
        Assert.Equal(normal, released);
    }

    [Fact]
    public void Undo_follows_the_backup_while_held_without_appearing_and_shows_once_released()
    {
        var (heldNoBackup, heldBackup, releasedBackup, releasedNoBackup) = With(w =>
        {
            w.SetEapoHeldOnCable(true);
            w.SetEapoUndoAvailable(false);
            var a = w.UndoRepairButton.Visibility;
            w.SetEapoUndoAvailable(true);   // a backup appears while held: still hidden
            var b = w.UndoRepairButton.Visibility;
            w.SetEapoHeldOnCable(false);    // ...and is offered as soon as the default is back
            var c = w.UndoRepairButton.Visibility;
            w.SetEapoUndoAvailable(false);
            return (a, b, c, w.UndoRepairButton.Visibility);
        });
        _out.WriteLine($"held, no backup: {heldNoBackup}; held, backup: {heldBackup}; "
            + $"released, backup: {releasedBackup}; released, no backup: {releasedNoBackup}");
        Assert.Equal(Visibility.Collapsed, heldNoBackup);
        Assert.Equal(Visibility.Collapsed, heldBackup);
        Assert.Equal(Visibility.Visible, releasedBackup);
        Assert.Equal(Visibility.Collapsed, releasedNoBackup);
    }
}
