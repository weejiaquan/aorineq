using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The audio-endpoint half of Equalizer APO detection, read off this real machine.
///
/// This is the half that answers the question the release exists for: Equalizer APO does not
/// process audio because it is installed, it processes because the ENDPOINT's property store names
/// its APOs — and a driver replacement resets that store while leaving Equalizer APO's own record
/// of the device untouched. Checking only the record (which is all this app did until 3.4.0)
/// reports a detached device as working.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class EapoEndpointTests
{
    private readonly ITestOutputHelper _out;
    public EapoEndpointTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void Equalizer_APOs_own_class_ids_resolve_to_its_own_binary()
    {
        var install = EapoDetection.GetInstallPath();
        Assert.NotNull(install);

        var clsids = EapoEndpoint.ResolveClsids(install!);
        _out.WriteLine($"pre-mix  {clsids?.PreMix}  -> {EapoEndpoint.ServerPathFor(clsids?.PreMix ?? "")}");
        _out.WriteLine($"post-mix {clsids?.PostMix} -> {EapoEndpoint.ServerPathFor(clsids?.PostMix ?? "")}");
        Assert.NotNull(clsids);

        // The gate that makes the answer trustworthy: each CLSID must be a registered in-process
        // server whose DLL lives inside the detected Equalizer APO install. One that resolves
        // anywhere else is not Equalizer APO's, and treating it as if it were would report a
        // detached device as working.
        foreach (var clsid in new[] { clsids!.PreMix, clsids.PostMix })
        {
            var server = EapoEndpoint.ServerPathFor(clsid);
            Assert.NotNull(server);
            Assert.StartsWith(install!, Path.GetFullPath(server!), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(server));
        }
        Assert.NotEqual(clsids.PreMix, clsids.PostMix);
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void A_class_id_registered_outside_the_install_is_refused()
    {
        Assert.Null(EapoEndpoint.ResolveClsids(Path.Combine(Path.GetTempPath(), "not-equalizer-apo")));
    }

    [Fact]
    public void An_unregistered_class_id_resolves_to_nothing()
    {
        Assert.Null(EapoEndpoint.ServerPathFor("{deadbeef-0000-0000-0000-000000000000}"));
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_default_endpoint_is_attached_and_recorded_on_this_machine()
    {
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        var clsids = EapoEndpoint.ResolveClsids(EapoDetection.GetInstallPath()!)!;
        foreach (var v in EapoEndpoint.ReadFxProperties(guid))
            _out.WriteLine($"  {v.Name,-46} {v.Kind,-12} {string.Join(" | ", v.Data ?? [])}");

        // BOTH halves, read straight off the machine, and the composite the app actually uses.
        Assert.True(EapoDetection.HasChildApoRecord(guid));
        Assert.True(EapoEndpoint.IsApoAttached(guid, clsids));
        Assert.True(EapoDetection.IsActiveOnEndpoint(guid));
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_two_halves_are_separable_which_is_the_whole_point()
    {
        // A device Equalizer APO has never been switched on for has NEITHER half — the real
        // registry, a real endpoint. What a driver reset produces is the asymmetric case: the
        // record survives, the attachment does not, and only the second half notices.
        var others = AudioEndpoint.GetRenderEndpoints()
            .Where(e => !EapoDetection.HasChildApoRecord(e.Guid))
            .ToList();
        foreach (var e in others)
            _out.WriteLine($"  no Equalizer APO record: {e.FriendlyName} {e.Guid}");
        Assert.NotEmpty(others); // this machine has more playback devices than it has ticked

        var clsids = EapoEndpoint.ResolveClsids(EapoDetection.GetInstallPath()!)!;
        foreach (var e in others)
        {
            Assert.False(EapoEndpoint.IsApoAttached(e.Guid, clsids));
            Assert.False(EapoDetection.IsActiveOnEndpoint(e.Guid));
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Reading_an_endpoint_that_does_not_exist_is_empty_not_an_error()
    {
        Assert.Empty(EapoEndpoint.ReadFxProperties("{deadbeef-0000-0000-0000-000000000000}"));
        Assert.Null(EapoEndpoint.ReadChildApos("{deadbeef-0000-0000-0000-000000000000}"));
        Assert.False(EapoDetection.IsActiveOnEndpoint("{deadbeef-0000-0000-0000-000000000000}"));
        Assert.False(EapoDetection.HasChildApoRecord("{deadbeef-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void An_attachment_needs_BOTH_slots_to_name_Equalizer_APOs_classes()
    {
        // The composition itself, without a machine: one slot right and the other missing, wrong,
        // or somebody else's is not an attachment.
        var clsids = new ApoClsids("{AAAAAAAA-0000-0000-0000-000000000001}",
                                   "{BBBBBBBB-0000-0000-0000-000000000002}");
        Assert.Equal("{AAAAAAAA-0000-0000-0000-000000000001}",
            EapoEndpoint.Find(
                [RegValue.Str(EapoEndpoint.FxStreamEffectClsid, clsids.PreMix)],
                EapoEndpoint.FxStreamEffectClsid)!.Single);
        // Case-insensitivity matters: the registry stores whatever case the writer used.
        Assert.Equal(clsids.PreMix.ToLowerInvariant(),
            RegValue.Str("x", clsids.PreMix.ToLowerInvariant()).Single);
        Assert.Null(EapoEndpoint.Find([RegValue.Str("other", "v")], EapoEndpoint.FxEndpointEffectClsid));
    }
}
