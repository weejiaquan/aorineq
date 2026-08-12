using Microsoft.Win32;

namespace AorinEQ.Core;

/// <summary>One registry value as found — name, kind and data — for reading an endpoint's effect
/// settings and reporting them. <see cref="Kind"/> is a string rather than
/// <see cref="RegistryValueKind"/> so a kind this code does not understand is still describable
/// instead of being silently coerced into one that looks similar.</summary>
public sealed record RegValue(string Name, string Kind, string[]? Data)
{
    public const string KindString = "String";
    public const string KindExpandString = "ExpandString";
    public const string KindMultiString = "MultiString";

    public static RegValue Str(string name, string value) => new(name, KindString, [value]);

    /// <summary>The single string this value holds, or null when it does not hold exactly one.</summary>
    public string? Single => Kind is KindString or KindExpandString && Data is { Length: 1 } ? Data[0] : null;
}

/// <summary>The two COM classes Equalizer APO installs on an endpoint: its pre-mix (per-stream)
/// APO and its post-mix (endpoint) APO.</summary>
public sealed record ApoClsids(string PreMix, string PostMix);

/// <summary>The audio endpoint side of Equalizer APO — the half that lives in Windows rather than
/// in Equalizer APO, and the half a Windows update resets.
///
/// Equalizer APO does not run because it is installed. It runs because the ENDPOINT's own property
/// store names its APOs: the CLSID of its pre-mix class in PKEY_FX_StreamEffectClsid and of its
/// post-mix class in PKEY_FX_EndpointEffectClsid, under
/// <c>MMDevices\Audio\Render\{guid}\FxProperties</c>. A driver replacement or reinstall resets that
/// store, and Equalizer APO's own record of the device — its <c>Child APOs</c> key — survives
/// untouched. That asymmetry is the whole failure this release detects: the device still looks
/// registered to anything that asks Equalizer APO, and is not processing.
///
/// Everything here READS. The measured ACL grants BUILTIN\Users read on FxProperties and ReadKey on
/// <c>Child APOs</c>; writing either needs Administrators.</summary>
public static class EapoEndpoint
{
    public const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    public const string ChildApoRoot = @"SOFTWARE\EqualizerAPO\Child APOs";

    private const string FxFmtId = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";

    /// <summary>PKEY_FX_StreamEffectClsid — where Equalizer APO's PRE-MIX class goes.</summary>
    public const string FxStreamEffectClsid = FxFmtId + ",5";
    /// <summary>PKEY_FX_EndpointEffectClsid — where Equalizer APO's POST-MIX class goes.</summary>
    public const string FxEndpointEffectClsid = FxFmtId + ",7";

    /// <summary>Equalizer APO's published APO CLSIDs, used only as a FALLBACK and only after being
    /// proven to resolve to a COM server inside the detected install (see
    /// <see cref="ResolveClsids"/>).</summary>
    public const string KnownPreMixClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    public const string KnownPostMixClsid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";

    private static RegistryKey BaseKey() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Every value under an endpoint's FxProperties, as found. An endpoint with no
    /// FxProperties key at all reads as an empty list — which is also what a driver reset leaves
    /// behind.</summary>
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

    /// <summary>Equalizer APO's own record for an endpoint, or null when it has none.</summary>
    public static IReadOnlyList<RegValue>? ReadChildApos(string endpointGuid)
    {
        try
        {
            using var baseKey = BaseKey();
            using var key = baseKey.OpenSubKey($@"{ChildApoRoot}\{endpointGuid}");
            return key?.GetValueNames().Select(n => Capture(key, n)).ToList();
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
            RegistryValueKind.String when data is string s => new RegValue(name, RegValue.KindString, [s]),
            RegistryValueKind.ExpandString when data is string s =>
                new RegValue(name, RegValue.KindExpandString, [s]),
            RegistryValueKind.MultiString when data is string[] m =>
                new RegValue(name, RegValue.KindMultiString, m),
            _ => new RegValue(name, "Unsupported:" + kind, null),
        };
    }

    /// <summary>Whether Equalizer APO is actually wired into this endpoint's audio path — the two
    /// effect slots naming its APOs. This is the fact a driver reinstall destroys, and it is
    /// independent of Equalizer APO's own bookkeeping.</summary>
    public static bool IsApoAttached(string endpointGuid, ApoClsids clsids)
    {
        var fx = ReadFxProperties(endpointGuid);
        return Same(Find(fx, FxStreamEffectClsid)?.Single, clsids.PreMix)
            && Same(Find(fx, FxEndpointEffectClsid)?.Single, clsids.PostMix);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static RegValue? Find(IReadOnlyList<RegValue> values, string name) =>
        values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Equalizer APO's two APO CLSIDs on THIS machine.
    ///
    /// Preference order, and the order is the point: an endpoint Equalizer APO's own Configurator
    /// has already registered here names, by definition, the classes this install uses; the
    /// published CLSIDs are the fallback. Either way both are then proven to resolve to a
    /// registered in-process COM server whose DLL lives inside <paramref name="installPath"/> —
    /// a CLSID that resolves anywhere else is not Equalizer APO's, and treating it as if it were
    /// would report a device as working when it is not.</summary>
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
}
