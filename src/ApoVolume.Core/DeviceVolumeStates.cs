namespace ApoVolume.Core;

/// <summary>Per-device volume model for eapo mode: one <see cref="VolumeState"/> per endpoint,
/// with the ACTIVE state following the Windows default render device. A device seen for the
/// first time seeds from the legacy top-level Percent/Muted (single-device users keep their
/// exact pre-v2 behavior); devices with persisted per-device state restore it. Not thread-safe
/// by design — all access happens on the dispatcher thread, like <see cref="VolumeState"/>.</summary>
public sealed class DeviceVolumeStates
{
    private readonly Dictionary<string, VolumeState> _states = new();
    private readonly Dictionary<string, DeviceVolumeSetting> _persisted;
    private readonly int _seedPercent;
    private readonly bool _seedMuted;
    private readonly VolumeState _fallback;
    private int _stepPercent;

    /// <summary>State currently driven by volume keys/OSD/tray — the last
    /// <see cref="SwitchTo"/> target, or a seed-initialized fallback before any device (or
    /// while no audio device exists at all).</summary>
    public VolumeState Active { get; private set; }

    /// <summary>Endpoint id behind <see cref="Active"/>; null while on the fallback state.</summary>
    public string? ActiveId { get; private set; }

    public DeviceVolumeStates(Settings settings)
    {
        _seedPercent = settings.Percent;
        _seedMuted = settings.Muted;
        _stepPercent = settings.StepPercent;
        _persisted = settings.DeviceVolumes is null
            ? new Dictionary<string, DeviceVolumeSetting>()
            : new Dictionary<string, DeviceVolumeSetting>(settings.DeviceVolumes);
        _fallback = new VolumeState(_seedPercent, _seedMuted, _stepPercent);
        Active = _fallback;
    }

    public int StepPercent
    {
        get => _stepPercent;
        set
        {
            _stepPercent = value;
            _fallback.StepPercent = value;
            foreach (var state in _states.Values)
                state.StepPercent = value;
        }
    }

    /// <summary>Makes <paramref name="endpointId"/>'s state the active one (creating it from
    /// persisted state or the legacy seed on first sight) and returns it. Null switches to the
    /// device-less fallback state.</summary>
    public VolumeState SwitchTo(string? endpointId)
    {
        if (endpointId is null)
        {
            ActiveId = null;
            Active = _fallback;
            return _fallback;
        }
        if (!_states.TryGetValue(endpointId, out var state))
        {
            state = _persisted.TryGetValue(endpointId, out var saved)
                ? new VolumeState(saved.Percent, saved.Muted, _stepPercent)
                : new VolumeState(_seedPercent, _seedMuted, _stepPercent);
            _states[endpointId] = state;
        }
        ActiveId = endpointId;
        Active = state;
        return state;
    }

    /// <summary>Everything to persist: live state for every device seen this session, plus the
    /// stored state of devices never switched to (unplugged headphones must not lose their
    /// volume by being absent for one session).</summary>
    public IReadOnlyDictionary<string, DeviceVolumeSetting> Snapshot()
    {
        var snap = new Dictionary<string, DeviceVolumeSetting>(_persisted);
        foreach (var (id, state) in _states)
            snap[id] = new DeviceVolumeSetting(state.Percent, state.Muted);
        return snap;
    }
}
