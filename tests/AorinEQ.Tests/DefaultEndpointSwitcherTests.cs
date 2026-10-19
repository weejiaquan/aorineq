using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Moving Windows' default playback device and putting it back — for real, through
/// IPolicyConfig, asserting what Windows reports per role. Every switching test restores each role
/// it found, the same trade the device-change tests make.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class DefaultEndpointSwitcherTests
{
    private readonly ITestOutputHelper _out;
    public DefaultEndpointSwitcherTests(ITestOutputHelper output) => _out = output;

    private const string NoSuchEndpoint = "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}";
    private const string Cable = "{0.0.0.00000000}.{11111111-0000-0000-0000-000000000000}";
    private const string Speakers = "{0.0.0.00000000}.{22222222-0000-0000-0000-000000000000}";
    private const string Headphones = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000000}";
    private static readonly DefaultRenderRecord Recorded = new(Speakers, Speakers, Speakers);

    /// <summary>Each role back exactly as found — not via the code under test.</summary>
    private static void PutBack(DefaultRenderRecord original)
    {
        DefaultRenderDevice.SetDefaultAndSettle(original.Multimedia!);
        if (original.Console is { } console)
            DefaultEndpointSwitcher.SetDefault(console, AudioEndpoint.ERole.Console);
        if (original.Communications is { } comms)
            DefaultEndpointSwitcher.SetDefault(comms, AudioEndpoint.ERole.Communications);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Capture_reads_each_role_and_multimedia_is_the_app_wide_default()
    {
        var now = DefaultEndpointSwitcher.Capture();
        _out.WriteLine($"captured: {now}");
        Assert.NotNull(now.Console);
        Assert.NotNull(now.Communications);
        Assert.Equal(AudioEndpoint.GetDefaultRenderEndpointId(), now.Multimedia);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void Switching_moves_every_role_and_restore_puts_each_one_back()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var other = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(other);
        _out.WriteLine($"before: {before}; switching to {other!.FriendlyName}");
        try
        {
            Assert.True(DefaultEndpointSwitcher.SetDefaultForAllRoles(other.Id));
            var during = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"during: {during}");
            Assert.Equal(new DefaultRenderRecord(other.Id, other.Id, other.Id), during);

            var moved = DefaultEndpointSwitcher.Restore(before);
            var after = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"restore moved multimedia to {moved ?? "(no change)"}; after: {after}");
            Assert.Equal(before, after);
            Assert.Equal(before.Multimedia, moved);
        }
        finally
        {
            PutBack(before);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Restoring_to_an_endpoint_that_no_longer_exists_leaves_windows_choice_alone()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var moved = DefaultEndpointSwitcher.Restore(new DefaultRenderRecord(NoSuchEndpoint, NoSuchEndpoint, NoSuchEndpoint));
        var after = DefaultEndpointSwitcher.Capture();
        _out.WriteLine($"before {before}; restore of a vanished device moved {moved ?? "(nothing)"}; after {after}");
        Assert.Null(moved);
        Assert.Equal(before, after);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void Each_role_is_restored_only_when_its_own_endpoint_still_exists()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var other = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(other);
        try
        {
            var moved = DefaultEndpointSwitcher.Restore(new DefaultRenderRecord(NoSuchEndpoint, other!.Id, NoSuchEndpoint));
            var after = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"before {before}; partial record -> moved {moved}; after {after}");
            Assert.Equal(other.Id, after.Multimedia);
            Assert.Equal(before.Console, after.Console);
            Assert.Equal(before.Communications, after.Communications);
            Assert.Equal(other.Id, moved);
        }
        finally
        {
            PutBack(before);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Asking_windows_to_default_to_a_device_that_does_not_exist_fails_quietly()
    {
        var before = DefaultEndpointSwitcher.Capture();
        bool ok = DefaultEndpointSwitcher.SetDefaultForAllRoles(NoSuchEndpoint);
        _out.WriteLine($"SetDefaultForAllRoles(bogus) -> {ok}; default still {AudioEndpoint.GetDefaultRenderEndpointId()}");
        Assert.False(ok);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_real_switch_by_the_user_mid_stream_reads_as_their_override_and_ours_as_ours()
    {
        // Spec §6 "user-change-mid-stream path", driven through real switches and real reads of
        // Windows' default rather than made-up ids. The other endpoint stands in for the cable:
        // to Windows, and to Classify, that is all the cable is.
        var before = DefaultEndpointSwitcher.Capture();
        var standIn = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(standIn);
        try
        {
            // AorinEQ's switch, with `before` as the record it would hold: the default it lands on is ours.
            Assert.True(DefaultEndpointSwitcher.SetDefaultForAllRoles(standIn!.Id));
            var landed = AudioEndpoint.GetDefaultRenderEndpointId();
            var ours = DefaultEndpointSwitcher.Classify(landed, ownTargetId: standIn.Id, before, cableId: standIn.Id);
            _out.WriteLine($"our switch to {standIn.FriendlyName}: default now {landed} -> {ours}");
            Assert.Equal(standIn.Id, landed);
            Assert.Equal(DefaultChange.Ours, ours);

            // The user picks their own device back in Sound settings while the stream runs.
            DefaultRenderDevice.SetDefaultAndSettle(before.Multimedia!);
            var chosen = AudioEndpoint.GetDefaultRenderEndpointId();
            var theirs = DefaultEndpointSwitcher.Classify(chosen, ownTargetId: null, before, cableId: standIn.Id);
            _out.WriteLine($"user switched to {chosen} mid-stream -> {theirs}");
            Assert.Equal(before.Multimedia, chosen);
            Assert.Equal(DefaultChange.UserOverride, theirs);
        }
        finally
        {
            PutBack(before);
        }
    }

    // ---------------------------------------------------------------- classification (pure)

    [Fact]
    public void Our_own_switch_arriving_is_ours_whatever_else_is_true()
    {
        Assert.Equal(DefaultChange.Ours, DefaultEndpointSwitcher.Classify(Cable, Cable, Recorded, Cable));
        Assert.Equal(DefaultChange.Ours, DefaultEndpointSwitcher.Classify(Speakers, Speakers, null, null));
        _out.WriteLine("a notification for the endpoint we just switched to is ours (switch AND restore)");
    }

    [Fact]
    public void Without_a_record_nothing_is_ours_to_manage()
    {
        var change = DefaultEndpointSwitcher.Classify(Headphones, null, null, Cable);
        _out.WriteLine($"no record -> {change}");
        Assert.Equal(DefaultChange.Unrelated, change);
    }

    [Fact]
    public void A_late_duplicate_for_the_cable_is_still_ours()
    {
        var change = DefaultEndpointSwitcher.Classify(Cable, null, Recorded, Cable);
        _out.WriteLine($"cable is default, record held, no switch in flight -> {change}");
        Assert.Equal(DefaultChange.Ours, change);
    }

    [Fact]
    public void The_user_picking_another_output_mid_stream_is_their_decision()
    {
        var change = DefaultEndpointSwitcher.Classify(Headphones, null, Recorded, Cable);
        _out.WriteLine($"user chose headphones mid-stream -> {change}");
        Assert.Equal(DefaultChange.UserOverride, change);
    }

    [Fact]
    public void Losing_the_cable_mid_stream_is_classified_as_cable_lost_so_the_default_is_restored()
    {
        // Review focus: the user disables or uninstalls VB-Cable while streaming. Windows moves the
        // default to whatever it likes; the user's ORIGINAL device is what should come back.
        var disabled = DefaultEndpointSwitcher.Classify(Headphones, null, Recorded, cableId: null);
        var nothingLeft = DefaultEndpointSwitcher.Classify(null, null, Recorded, cableId: null);
        _out.WriteLine($"cable gone -> {disabled}; cable gone and no device at all -> {nothingLeft}");
        Assert.Equal(DefaultChange.CableLost, disabled);
        Assert.Equal(DefaultChange.CableLost, nothingLeft);
    }
}
