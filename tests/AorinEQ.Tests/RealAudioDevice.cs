using Xunit;

namespace AorinEQ.Tests;

/// <summary>Groups every test that drives the machine's REAL audio endpoints into one xunit
/// collection, so they can never run at the same time as each other.
///
/// There is one default playback device and one master volume on this machine, and these tests
/// change both. Two of them running together is not a slow test or a flaky product — it is two
/// tests writing the same global, and whichever asserts second reads the other's value.
///
/// The Equalizer APO tests belong here too even though they change nothing: EAPO is registered
/// PER ENDPOINT, so "is it active on this machine" is really "is it active on whichever device is
/// default right now". They read a global the device tests write. (Observed: a crash that skipped
/// a switching test's restore left the default on the endpoint EAPO is not registered on, and
/// those three tests failed for the rest of the session — correctly, and for a reason that had
/// nothing to do with them.)
///
/// Collections are already serialized today by parallelizeTestCollections=false in
/// xunit.runner.json, which is there for a different reason (a process-wide current-directory
/// collision). This attribute states the requirement where the requirement actually is, so that
/// re-enabling parallelism — a reasonable thing to want for the other 900 tests — cannot silently
/// reintroduce it.</summary>
[CollectionDefinition(Name)]
public sealed class RealAudioDeviceCollection
{
    public const string Name = "real audio device";
}
