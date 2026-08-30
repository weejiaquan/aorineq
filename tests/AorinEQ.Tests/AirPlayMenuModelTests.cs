using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Which items the AirPlay bar's dropdown offers.
///
/// This file exists because of a shipped defect that no screenshot could have caught: the menu
/// rendered, it was clickable, and on a cold start it offered Rescan and nothing else. Connect was
/// gated on the chosen receiver being in the DISCOVERED list, and nothing discovers until the user
/// asks - and this menu is where they would ask. So the receiver named in the settings file was
/// unreachable from the strip until you pressed Rescan, waited, and opened the menu again.</summary>
public class AirPlayMenuModelTests
{
    private const string Chosen = "BE4DBCD7755B@Bedroom";
    private const string Other = "AABBCCDDEEFF@Kitchen";

    private readonly ITestOutputHelper _out;

    public AirPlayMenuModelTests(ITestOutputHelper output) => _out = output;

    private AirPlayMenuModel Model(int devices, string? current, string? chosen)
    {
        var m = AirPlayMenuModel.For(devices, current, chosen);
        _out.WriteLine($"devices={devices} current={current ?? "(none)"} chosen={chosen ?? "(none)"}" +
                       $" -> noneFound={m.NoneFound} disconnect={m.Disconnect} connect={m.Connect}");
        return m;
    }

    /// <summary>THE REGRESSION. Nothing discovered, but a receiver was chosen in a previous
    /// session: Connect has to be there, because it is the only way back to it.</summary>
    [Fact]
    public void Connect_is_offered_before_anything_has_been_discovered()
    {
        var m = Model(devices: 0, current: null, chosen: Chosen);

        Assert.True(m.Connect);
        Assert.True(m.NoneFound); // said out loud, and not a reason to withhold Connect
        Assert.False(m.Disconnect);
    }

    /// <summary>The empty list is a fact about discovery, not about the user's choice, so it must
    /// not change what Connect does.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void How_many_receivers_were_found_does_not_decide_whether_connect_is_offered(int found)
    {
        Assert.True(Model(found, current: null, chosen: Chosen).Connect);
    }

    /// <summary>Nothing chosen, nothing found: only Rescan is honest. Offering Connect here would
    /// be a button with no receiver behind it.</summary>
    [Fact]
    public void A_fresh_install_offers_neither()
    {
        var m = Model(devices: 0, current: null, chosen: null);

        Assert.False(m.Connect);
        Assert.False(m.Disconnect);
        Assert.True(m.NoneFound);
    }

    /// <summary>Receivers on the network but none picked yet - the rows are the way in, so there is
    /// nothing for a Connect item to connect to.</summary>
    [Fact]
    public void Devices_found_but_none_chosen_offers_neither()
    {
        var m = Model(devices: 3, current: null, chosen: null);

        Assert.False(m.Connect);
        Assert.False(m.Disconnect);
        Assert.False(m.NoneFound);
    }

    /// <summary>A live session gets Disconnect.</summary>
    [Fact]
    public void A_live_session_offers_disconnect()
    {
        var m = Model(devices: 1, current: Chosen, chosen: Chosen);

        Assert.True(m.Disconnect);
        Assert.False(m.Connect);
    }

    /// <summary>Never both. They are the same row, and showing the pair says nothing about which
    /// state you are actually in.</summary>
    [Theory]
    [InlineData(0, null, null)]
    [InlineData(0, null, Chosen)]
    [InlineData(2, null, Chosen)]
    [InlineData(2, Chosen, Chosen)]
    [InlineData(2, Other, Chosen)]
    [InlineData(2, Chosen, null)]
    public void Connect_and_disconnect_are_never_offered_together(
        int devices, string? current, string? chosen)
    {
        var m = Model(devices, current, chosen);
        Assert.False(m.Connect && m.Disconnect);
    }

    /// <summary>Connected to one receiver while settings name another - which happens after
    /// picking a new one from the tray. The session you HAVE is what a menu can end, so Disconnect
    /// wins; Connect to the other is the row it already has.</summary>
    [Fact]
    public void Connected_elsewhere_offers_disconnect_not_connect()
    {
        var m = Model(devices: 2, current: Other, chosen: Chosen);

        Assert.True(m.Disconnect);
        Assert.False(m.Connect);
    }

    /// <summary>An empty string is how "no receiver" reaches this from a settings file, and it must
    /// not read as a chosen one - that would put a Connect item on a fresh install with nothing
    /// behind it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_chosen_id_is_not_a_chosen_receiver(string? chosen)
    {
        Assert.False(Model(devices: 0, current: null, chosen: chosen).Connect);
    }

    /// <summary>Same for the session id: empty means no session, so it must not suppress Connect
    /// and offer a Disconnect that would end nothing.</summary>
    [Fact]
    public void An_empty_current_id_is_not_a_live_session()
    {
        var m = Model(devices: 1, current: "", chosen: Chosen);

        Assert.False(m.Disconnect);
        Assert.True(m.Connect);
    }
}

/// <summary>What the AirPlay strip's power button would do, and whether it is drawn at all.
///
/// Same pair of facts the dropdown's Connect/Disconnect rows are built from, deliberately: the
/// button and the menu are two ways to ask for the same thing, and they must never disagree about
/// which one is available.</summary>
public class AirPlayPowerTests
{
    private readonly ITestOutputHelper _out;

    public AirPlayPowerTests(ITestOutputHelper output) => _out = output;

    private AirPlayPower Power(string name, bool connected)
    {
        var state = new AirPlayBarState(true, name, connected, connected, 50);
        var p = state.Power;
        _out.WriteLine($"name='{name}' connected={connected} -> {p}");
        return p;
    }

    /// <summary>A session to end.</summary>
    [Fact]
    public void Connected_means_the_button_disconnects() =>
        Assert.Equal(AirPlayPower.Disconnect, Power("Bedroom", connected: true));

    /// <summary>Chosen but not connected - after a restart, or once disconnected. This is the case
    /// the button exists for: one tap instead of a menu.</summary>
    [Fact]
    public void A_chosen_receiver_and_no_session_means_the_button_connects() =>
        Assert.Equal(AirPlayPower.Connect, Power("Bedroom", connected: false));

    /// <summary>Nothing chosen: there is no receiver for a power button to act on, so it is not
    /// drawn and the tap falls through to the dropdown - which is where a first one gets picked.
    /// Drawing it here would be a switch with nothing behind it.</summary>
    [Fact]
    public void No_receiver_chosen_means_no_button()
    {
        Assert.Equal(AirPlayPower.Unavailable, Power("", connected: false));
    }

    /// <summary>A hidden bar has no button either, and asking is not an error.</summary>
    [Fact]
    public void The_hidden_state_has_no_button()
    {
        _out.WriteLine($"Hidden -> {AirPlayBarState.Hidden.Power}");
        Assert.Equal(AirPlayPower.Unavailable, AirPlayBarState.Hidden.Power);
    }

    /// <summary>The button and the menu agree. Connect is offered by exactly one of them in the
    /// same circumstances, which is the property that stops the strip saying two different things
    /// about the same receiver.</summary>
    [Theory]
    [InlineData("Bedroom", true)]
    [InlineData("Bedroom", false)]
    [InlineData("", false)]
    public void The_button_and_the_menu_never_disagree(string name, bool connected)
    {
        var state = new AirPlayBarState(true, name, connected, connected, 50);
        var menu = AirPlayMenuModel.For(
            deviceCount: 0,
            currentId: connected ? "id@" + name : null,
            chosenId: name.Length > 0 ? "id@" + name : null);

        _out.WriteLine($"button={state.Power}  menu connect={menu.Connect} disconnect={menu.Disconnect}");

        Assert.Equal(menu.Connect, state.Power == AirPlayPower.Connect);
        Assert.Equal(menu.Disconnect, state.Power == AirPlayPower.Disconnect);
    }
}
