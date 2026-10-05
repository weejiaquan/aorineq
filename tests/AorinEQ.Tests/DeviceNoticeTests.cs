using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>When a change of the default playback device is worth saying out loud, and what is
/// said. The window that draws the notice is handed the string these tests assert on and nothing
/// else, so everything about WHETHER lives here and can be run with no audio device attached.</summary>
public class DeviceNoticeTests
{
    private const string DevA = "{0.0.0.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string DevB = "{0.0.0.00000000}.{bbbbbbbb-5555-6666-7777-888888888888}";
    private const string NoDevice = "No output device";
    private const string Unnamed = "Output device changed";

    private readonly ITestOutputHelper _out;
    public DeviceNoticeTests(ITestOutputHelper output) => _out = output;

    private string? Next(DeviceNotice notice, string? id, string? name, bool enabled = true)
    {
        var text = notice.Next(enabled, id, name, NoDevice, Unnamed);
        _out.WriteLine($"enabled={enabled} id={id ?? "(none)"} name='{name}' -> {(text is null ? "(no notice)" : $"'{text}'")}");
        return text;
    }

    [Fact]
    public void Starting_on_a_device_is_not_a_switch()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Null(Next(notice, DevA, "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void A_switch_names_the_device_switched_to()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Equal("Headphones (USB DAC)", Next(notice, DevB, "Headphones (USB DAC)"));
    }

    [Fact]
    public void One_switch_reported_once_per_role_is_one_notice()
    {
        // Console, Multimedia, Communications: Windows can say the same thing three times.
        var notice = new DeviceNotice(DevA);

        Assert.Equal("Headphones", Next(notice, DevB, "Headphones"));
        Assert.Null(Next(notice, DevB, "Headphones"));
        Assert.Null(Next(notice, DevB, "Headphones"));
    }

    [Fact]
    public void Switching_back_is_a_switch_of_its_own()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Equal("Headphones", Next(notice, DevB, "Headphones"));
        Assert.Equal("Speakers", Next(notice, DevA, "Speakers"));
    }

    [Fact]
    public void A_round_trip_that_ends_where_it_started_says_nothing()
    {
        // A -> B -> A faster than the notification queue drains: both notifications re-read the
        // current default and both find A, which is where the app already was.
        var notice = new DeviceNotice(DevA);

        Assert.Null(Next(notice, DevA, "Speakers"));
        Assert.Null(Next(notice, DevA, "Speakers"));
    }

    [Fact]
    public void Nothing_is_shown_while_the_setting_is_off()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Null(Next(notice, DevB, "Headphones", enabled: false));
    }

    [Fact]
    public void Turning_the_setting_on_does_not_announce_a_switch_that_already_happened()
    {
        var notice = new DeviceNotice(DevA);
        Next(notice, DevB, "Headphones", enabled: false);

        Assert.Null(Next(notice, DevB, "Headphones", enabled: true));
        Assert.Equal("Speakers", Next(notice, DevA, "Speakers", enabled: true));
    }

    [Fact]
    public void Losing_every_device_says_so_once()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Equal(NoDevice, Next(notice, null, ""));
        Assert.Null(Next(notice, null, ""));
    }

    [Fact]
    public void A_device_arriving_after_a_start_with_none_is_announced()
    {
        var notice = new DeviceNotice(null);

        Assert.Null(Next(notice, null, ""));
        Assert.Equal("Speakers", Next(notice, DevA, "Speakers"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_device_whose_name_cannot_be_read_is_still_announced(string? name)
    {
        var notice = new DeviceNotice(DevA);

        Assert.Equal(Unnamed, Next(notice, DevB, name));
    }

    [Fact]
    public void Whitespace_around_a_name_is_not_shown()
    {
        var notice = new DeviceNotice(DevA);

        Assert.Equal("Headphones", Next(notice, DevB, "  Headphones \t"));
    }

    [Fact]
    public void A_long_name_is_cut_before_it_is_shown()
    {
        var name = "Headphones (" + new string('x', 80) + ")";
        var notice = new DeviceNotice(DevA);

        var text = Next(notice, DevB, name);

        Assert.NotNull(text);
        Assert.Equal(DeviceNotice.MaxNameLength, text.Length);
        Assert.StartsWith("Headphones (xxx", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Truncate_leaves_a_name_that_fits_alone()
    {
        var exact = new string('a', DeviceNotice.MaxNameLength);

        Assert.Equal("Speakers (Realtek High Definition Audio)",
            DeviceNotice.Truncate("Speakers (Realtek High Definition Audio)"));
        Assert.Equal(exact, DeviceNotice.Truncate(exact));
        Assert.Equal("", DeviceNotice.Truncate(""));
    }

    [Fact]
    public void Truncate_keeps_the_start_and_ends_in_an_ellipsis()
    {
        var name = string.Concat(Enumerable.Range(0, 60).Select(i => (char)('a' + i % 26)));

        var cut = DeviceNotice.Truncate(name);

        _out.WriteLine($"{name.Length} chars -> {cut.Length}: '{cut}'");
        Assert.Equal(DeviceNotice.MaxNameLength, cut.Length);
        Assert.Equal(name[..(DeviceNotice.MaxNameLength - 1)] + "…", cut);
    }

    [Fact]
    public void Truncate_leaves_no_space_dangling_before_the_ellipsis()
    {
        // The cut lands just after a space; "Realtek …" reads as a typo.
        var name = new string('a', DeviceNotice.MaxNameLength - 2) + " bbbbbbbb";

        var cut = DeviceNotice.Truncate(name);

        Assert.Equal(new string('a', DeviceNotice.MaxNameLength - 2) + "…", cut);
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        // The pair straddles the cut: its high half is the last character that would be kept.
        var name = new string('a', DeviceNotice.MaxNameLength - 2) + "\U0001F3A7" + new string('b', 10);

        var cut = DeviceNotice.Truncate(name);

        _out.WriteLine($"cut to {cut.Length} chars: '{cut}'");
        Assert.Equal(new string('a', DeviceNotice.MaxNameLength - 2) + "…", cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }
}
