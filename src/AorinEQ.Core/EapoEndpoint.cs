using Microsoft.Win32;

namespace AorinEQ.Core;

/// <summary>One registry value captured exactly as it was found — including the case that matters
/// most for a faithful undo: it was not there at all.
///
/// <see cref="Kind"/> is a string rather than <see cref="RegistryValueKind"/> so a backup written
/// by one build is still readable by the next, and so an unexpected kind survives a round trip
/// through JSON as something recognisably unrepresentable rather than as a silently wrong
/// value.</summary>
public sealed record RegValue(string Name, string Kind, string[]? Data)
{
    public const string KindString = "String";
    public const string KindMultiString = "MultiString";
    /// <summary>The value did not exist. Restoring it means DELETING whatever is there now.</summary>
    public const string KindAbsent = "Absent";

    public static RegValue Absent(string name) => new(name, KindAbsent, null);
    public static RegValue Str(string name, string value) => new(name, KindString, new[] { value });
    public static RegValue Multi(string name, params string[] values) => new(name, KindMultiString, values);

    /// <summary>Whether this build can write this value back. A backup is only worth taking if
    /// every value in it can be restored, so anything else must stop a repair before it starts.</summary>
    public bool IsRestorable => Kind is KindAbsent or KindString or KindMultiString;

    public string? Single => Kind == KindString && Data is { Length: 1 } ? Data[0] : null;
}

/// <summary>The two COM classes Equalizer APO installs on an endpoint: its pre-mix (per-stream)
/// APO and its post-mix (endpoint) APO.</summary>
public sealed record ApoClsids(string PreMix, string PostMix);

/// <summary>The audio endpoint side of Equalizer APO — the half that lives in Windows rather than
/// in Equalizer APO, and the half a Windows update resets.
///
/// WHAT EQUALIZER APO ACTUALLY DOES TO AN ENDPOINT. Its Configurator writes into the endpoint's
/// property store, <c>MMDevices\Audio\Render\{guid}\FxProperties</c>: the effect association
/// (GUID_NULL), a display name, the CLSID of its pre-mix APO in PKEY_FX_StreamEffectClsid and of
/// its post-mix APO in PKEY_FX_EndpointEffectClsid, and — so Windows will actually run them — the
/// list of signal-processing modes each supports. It then records what it displaced under
/// <c>HKLM\SOFTWARE\EqualizerAPO\Child APOs\{guid}</c>, using the sentinel <c>!VALUE</c> for
/// "there was nothing here", together with the APOs to chain to and a format version.
///
/// Every name and value below was read off THIS project's machine, from an endpoint Equalizer
/// APO's own Configurator had registered — not from documentation. The property keys agree with
/// the documented PKEY_FX_* and PKEY_*FX_ProcessingModes_Supported_For_Streaming definitions,
/// which is the cross-check that they mean what they appear to mean.
///
/// READING is unprivileged. WRITING needs Administrators: the measured ACL on FxProperties grants
/// BUILTIN\Users read, and <c>Child APOs</c> grants Users ReadKey only.</summary>
public static class EapoEndpoint
{
    public const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    public const string ChildApoRoot = @"SOFTWARE\EqualizerAPO\Child APOs";

    private const string FxFmtId = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private const string ModesFmtId = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";

    /// <summary>PKEY_FX_Association.</summary>
    public const string FxAssociation = FxFmtId + ",0";
    /// <summary>PKEY_FX_PreMixClsid — the legacy LFX slot. Equalizer APO leaves it alone; it is
    /// backed up because it is one of the five slots its own Child APOs record covers.</summary>
    public const string FxPreMixClsid = FxFmtId + ",1";
    /// <summary>PKEY_FX_PostMixClsid — the legacy GFX slot.</summary>
    public const string FxPostMixClsid = FxFmtId + ",2";
    /// <summary>PKEY_FX_StreamEffectClsid — where Equalizer APO's PRE-MIX class goes.</summary>
    public const string FxStreamEffectClsid = FxFmtId + ",5";
    /// <summary>PKEY_FX_ModeEffectClsid.</summary>
    public const string FxModeEffectClsid = FxFmtId + ",6";
    /// <summary>PKEY_FX_EndpointEffectClsid — where Equalizer APO's POST-MIX class goes.</summary>
    public const string FxEndpointEffectClsid = FxFmtId + ",7";
    /// <summary>PKEY_ItemNameDisplay for the effect set.</summary>
    public const string FxDisplayName = "{b725f130-47ef-101a-a5f1-02608c9eebac},10";
    public const string SfxProcessingModes = ModesFmtId + ",5";
    public const string MfxProcessingModes = ModesFmtId + ",6";
    public const string EfxProcessingModes = ModesFmtId + ",7";

    /// <summary>AUDIO_SIGNALPROCESSINGMODE_DEFAULT — the one mode Equalizer APO declares support
    /// for, and the mode ordinary playback uses.</summary>
    public const string DefaultProcessingMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";

    /// <summary>The display name Equalizer APO's Configurator writes. A stock Windows effect-set
    /// name, reproduced verbatim rather than invented, so a repaired endpoint is indistinguishable
    /// from a Configurator-registered one.</summary>
    public const string EffectsDisplayName = "Microsoft Audio Home Theater Effects";

    /// <summary>Equalizer APO's sentinel for "this slot was empty before I took it".</summary>
    public const string NoValueSentinel = "!VALUE";

    /// <summary>Child APOs record version written by Equalizer APO 1.2/1.3.</summary>
    public const string ChildApoVersion = "2";

    /// <summary>The five FxProperties slots Equalizer APO records originals for.</summary>
    public static readonly IReadOnlyList<string> ChainedSlots =
        [FxPreMixClsid, FxPostMixClsid, FxStreamEffectClsid, FxModeEffectClsid, FxEndpointEffectClsid];

    /// <summary>Every FxProperties value a repair writes. Also the list a revert deletes, for a
    /// backup that recorded them all as absent.</summary>
    public static readonly IReadOnlyList<string> WrittenFxValues =
        [FxAssociation, FxDisplayName, SfxProcessingModes, MfxProcessingModes, EfxProcessingModes,
         FxStreamEffectClsid, FxEndpointEffectClsid];

    /// <summary>Equalizer APO's own product CLSIDs, used only as a FALLBACK and only after being
    /// proven to resolve to a COM server inside the detected install (see
    /// <see cref="ResolveClsids"/>). Preferring an existing registration on the same machine over
    /// these is what keeps the repair a replication rather than a guess.</summary>
    public const string KnownPreMixClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    public const string KnownPostMixClsid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";

    private static RegistryKey BaseKey() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Every value under an endpoint's FxProperties, as found. An endpoint with no
    /// FxProperties key at all reads as an empty list — indistinguishable, for our purposes, from
    /// one whose FxProperties is empty, and both are the "bare" shape a repair can handle.</summary>
    public static IReadOnlyList<RegValue> ReadFxProperties(string endpointGuid)
    {
        var result = new List<RegValue>();
        try
        {
            using var baseKey = BaseKey();
            using var key = baseKey.OpenSubKey($@"{RenderRoot}\{endpointGuid}\FxProperties");
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
                result.Add(Capture(key, name));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
        }
        return result;
    }

    /// <summary>The Child APOs record for an endpoint, or null when Equalizer APO has none.</summary>
    public static IReadOnlyList<RegValue>? ReadChildApos(string endpointGuid)
    {
        try
        {
            using var baseKey = BaseKey();
            using var key = baseKey.OpenSubKey($@"{ChildApoRoot}\{endpointGuid}");
            if (key is null) return null;
            return key.GetValueNames().Select(n => Capture(key, n)).ToList();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RegValue Capture(RegistryKey key, string name)
    {
        var kind = key.GetValueKind(name);
        var data = key.GetValue(name);
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString when data is string s =>
                new RegValue(name, RegValue.KindString, [s]),
            RegistryValueKind.MultiString when data is string[] m =>
                new RegValue(name, RegValue.KindMultiString, m),
            // Anything else is recorded honestly as something this build cannot write back, which
            // is what makes CanRepair refuse rather than risk an unrestorable change.
            _ => new RegValue(name, "Unsupported:" + kind, null),
        };
    }

    /// <summary>Whether Equalizer APO is actually wired into this endpoint's audio path — the two
    /// effect slots naming its APOs. This is the fact a driver reinstall destroys, and it is
    /// independent of Equalizer APO's own bookkeeping: an endpoint can keep a Child APOs record
    /// long after the property store that made it real was reset.</summary>
    public static bool IsApoAttached(string endpointGuid, ApoClsids clsids)
    {
        var fx = ReadFxProperties(endpointGuid);
        return Equals(Find(fx, FxStreamEffectClsid)?.Single, clsids.PreMix)
            && Equals(Find(fx, FxEndpointEffectClsid)?.Single, clsids.PostMix);
    }

    private static bool Equals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static RegValue? Find(IReadOnlyList<RegValue> values, string name) =>
        values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Equalizer APO's two APO CLSIDs on THIS machine.
    ///
    /// Preference order, and the order is the safety argument:
    /// <list type="number">
    /// <item>an endpoint Equalizer APO's own Configurator has already registered here — its
    /// values are by definition the ones the Configurator writes on this install;</item>
    /// <item>the product's published CLSIDs.</item>
    /// </list>
    /// Either way both are then proven to resolve to a registered in-process COM server whose DLL
    /// lives inside <paramref name="installPath"/>. Null means "do not write anything" — a CLSID
    /// that does not resolve to Equalizer APO's own binary is exactly the value that would leave
    /// an endpoint pointing at an effect that cannot load.</summary>
    public static ApoClsids? ResolveClsids(string installPath)
    {
        var discovered = DiscoverFromRegisteredEndpoint();
        if (discovered is not null && BothResolveInside(discovered, installPath))
            return discovered;

        var known = new ApoClsids(KnownPreMixClsid, KnownPostMixClsid);
        return BothResolveInside(known, installPath) ? known : null;
    }

    private static ApoClsids? DiscoverFromRegisteredEndpoint()
    {
        try
        {
            using var baseKey = BaseKey();
            using var childRoot = baseKey.OpenSubKey(ChildApoRoot);
            if (childRoot is null) return null;
            foreach (var guid in childRoot.GetSubKeyNames())
            {
                var fx = ReadFxProperties(guid);
                if (Find(fx, FxStreamEffectClsid)?.Single is { } pre
                    && Find(fx, FxEndpointEffectClsid)?.Single is { } post)
                    return new ApoClsids(pre, post);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool BothResolveInside(ApoClsids clsids, string installPath) =>
        ServerPathFor(clsids.PreMix) is { } a && IsInside(a, installPath)
        && ServerPathFor(clsids.PostMix) is { } b && IsInside(b, installPath);

    /// <summary>The in-process server registered for a CLSID, or null.</summary>
    public static string? ServerPathFor(string clsid)
    {
        try
        {
            using var baseKey = BaseKey();
            using var key = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsInside(string path, string directory)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim('"'));
            var root = Path.GetFullPath(directory);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Why a repair is refused, or null when it can go ahead.
    ///
    /// The rule is narrow ON PURPOSE. The shape this code can reproduce faithfully is the one it
    /// was read off: an endpoint whose five effect slots are EMPTY, which is exactly what a driver
    /// reinstall leaves behind and exactly what Equalizer APO's own record on this machine
    /// describes (all five originals recorded as "!VALUE"). An endpoint that already carries
    /// somebody else's effect has to be CHAINED to rather than overwritten, and how Equalizer APO
    /// chains is not something to infer from one machine — so that case is handed to the
    /// Configurator, which knows.</summary>
    public static string? WhyNotRepairable(IReadOnlyList<RegValue> fx, ApoClsids clsids)
    {
        if (WhyNotRestorable(fx) is { } unrestorable)
            return unrestorable;
        foreach (var slot in ChainedSlots)
        {
            if (Find(fx, slot) is not { } present) continue;
            // Already ours: a half-finished or already-good repair, which is repairable.
            if (Equals(present.Single, clsids.PreMix) || Equals(present.Single, clsids.PostMix))
                continue;
            return "this device already has another audio effect installed on it. Equalizer APO's "
                + "own Configurator knows how to add itself alongside it — use Configurator instead.";
        }
        return null;
    }

    /// <summary>Why this set of values could not be written back exactly as found, or null when it
    /// could. Applied to BOTH keys a repair touches, because a value that cannot be restored makes
    /// the REVERT throw — at the one moment it must not.</summary>
    public static string? WhyNotRestorable(IReadOnlyList<RegValue> values)
    {
        foreach (var value in values)
        {
            if (!value.IsRestorable)
                return $"this device has a setting AorinEQ can't safely put back ({value.Name}).";
        }
        return null;
    }

    /// <summary>Exactly what a repair writes to the endpoint, given the resolved CLSIDs.</summary>
    public static IReadOnlyList<RegValue> RepairFxValues(ApoClsids clsids) =>
    [
        RegValue.Str(FxAssociation, "{00000000-0000-0000-0000-000000000000}"),
        RegValue.Str(FxDisplayName, EffectsDisplayName),
        RegValue.Multi(SfxProcessingModes, DefaultProcessingMode),
        RegValue.Multi(MfxProcessingModes, DefaultProcessingMode),
        RegValue.Multi(EfxProcessingModes, DefaultProcessingMode),
        RegValue.Str(FxStreamEffectClsid, clsids.PreMix),
        RegValue.Str(FxEndpointEffectClsid, clsids.PostMix),
    ];

    /// <summary>Exactly what a repair writes to Equalizer APO's own record. The five originals are
    /// all the sentinel because <see cref="WhyNotRepairable"/> has already established the slots
    /// were empty — writing anything else would tell Equalizer APO to chain to an APO that is not
    /// there.</summary>
    public static IReadOnlyList<RegValue> RepairChildApoValues() =>
    [
        RegValue.Str(FxPreMixClsid, NoValueSentinel),
        RegValue.Str(FxPostMixClsid, NoValueSentinel),
        RegValue.Str(FxStreamEffectClsid, NoValueSentinel),
        RegValue.Str(FxModeEffectClsid, NoValueSentinel),
        RegValue.Str(FxEndpointEffectClsid, NoValueSentinel),
        RegValue.Str("PreMixChild", ""),
        RegValue.Str("PostMixChild", ""),
        RegValue.Str("AllowSilentBufferModification", "false"),
        RegValue.Str("Version", ChildApoVersion),
    ];

    // ------------------------------------------------------------------ writes (Administrators)

    /// <summary>Writes a set of values into an endpoint's FxProperties, creating the key if
    /// needed. Only ever called with <see cref="RepairFxValues"/> or a backup's own contents.</summary>
    public static void WriteFxProperties(string endpointGuid, IReadOnlyList<RegValue> values)
    {
        using var baseKey = BaseKey();
        using var key = baseKey.CreateSubKey($@"{RenderRoot}\{endpointGuid}\FxProperties", writable: true)
            ?? throw new InvalidOperationException("couldn't open the playback device's effect settings.");
        WriteValues(key, values);
    }

    public static void WriteChildApos(string endpointGuid, IReadOnlyList<RegValue> values)
    {
        using var baseKey = BaseKey();
        using var key = baseKey.CreateSubKey($@"{ChildApoRoot}\{endpointGuid}", writable: true)
            ?? throw new InvalidOperationException("couldn't open Equalizer APO's device list.");
        WriteValues(key, values);
    }

    /// <summary>Removes Equalizer APO's record for one endpoint. Establishes there is something to
    /// delete BEFORE asking for write access: "nothing to delete" is an answer an unprivileged
    /// caller can reach, and a revert that has nothing left to do must not fail for want of a
    /// right it does not need.</summary>
    public static void DeleteChildApos(string endpointGuid)
    {
        using var baseKey = BaseKey();
        using (var probe = baseKey.OpenSubKey($@"{ChildApoRoot}\{endpointGuid}"))
        {
            if (probe is null) return;
        }
        using var root = baseKey.OpenSubKey(ChildApoRoot, writable: true);
        root?.DeleteSubKeyTree(endpointGuid, throwOnMissingSubKey: false);
    }

    private static void WriteValues(RegistryKey key, IReadOnlyList<RegValue> values)
    {
        foreach (var value in values)
        {
            switch (value.Kind)
            {
                case RegValue.KindAbsent:
                    key.DeleteValue(value.Name, throwOnMissingValue: false);
                    break;
                case RegValue.KindString:
                    key.SetValue(value.Name, value.Data![0], RegistryValueKind.String);
                    break;
                case RegValue.KindMultiString:
                    key.SetValue(value.Name, value.Data!, RegistryValueKind.MultiString);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"refusing to write an unsupported registry value kind ({value.Kind}).");
            }
        }
    }

    /// <summary>Restores an endpoint's FxProperties to EXACTLY the captured state: every value
    /// that was there is put back with its own kind and data, and every value that is there NOW
    /// but was not then is deleted. That second half is what makes an undo an undo rather than a
    /// merge.</summary>
    public static void RestoreFxProperties(string endpointGuid, IReadOnlyList<RegValue> captured)
    {
        var restore = WithDeletionsFor(ReadFxProperties(endpointGuid), captured);
        if (restore.Count > 0)
            WriteFxProperties(endpointGuid, restore);
    }

    /// <summary>The same exact-restore for Equalizer APO's own record. Used when the record
    /// EXISTED before the repair; a record that did not exist is deleted outright instead.</summary>
    public static void RestoreChildApos(string endpointGuid, IReadOnlyList<RegValue> captured)
    {
        var restore = WithDeletionsFor(ReadChildApos(endpointGuid) ?? [], captured);
        if (restore.Count > 0)
            WriteChildApos(endpointGuid, restore);
    }

    private static List<RegValue> WithDeletionsFor(
        IReadOnlyList<RegValue> current, IReadOnlyList<RegValue> captured)
    {
        var wanted = captured.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restore = new List<RegValue>(captured);
        foreach (var value in current)
        {
            if (!wanted.Contains(value.Name))
                restore.Add(RegValue.Absent(value.Name));
        }
        return restore;
    }
}
