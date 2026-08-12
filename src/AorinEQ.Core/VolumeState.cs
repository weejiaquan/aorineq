namespace AorinEQ.Core;

/// <summary>Pure volume model: Windows-style percent mapped linearly in dB to an APO preamp value.</summary>
public sealed class VolumeState
{
    public const double MuteDb = -120.0;
    public const double MinDb = -50.0;
    private const int DefaultStepPercent = 2;

    public int Percent { get; private set; }
    public bool Muted { get; private set; }

    private int _stepPercent;
    public int StepPercent
    {
        get => _stepPercent;
        set
        {
            var validSteps = new[] { 1, 2, 5 };
            _stepPercent = Array.Exists(validSteps, x => x == value) ? value : DefaultStepPercent;
        }
    }

    public VolumeState(int percent = 50, bool muted = false, int stepPercent = 2)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Muted = muted;
        StepPercent = stepPercent; // uses the setter which clamps
    }

    public void Up()
    {
        Muted = false;
        Percent = Math.Min(100, Percent + StepPercent);
    }

    public void Down() => Percent = Math.Max(0, Percent - StepPercent);

    public void ToggleMute() => Muted = !Muted;

    public void SetPercent(int percent)
    {
        Percent = Math.Clamp(percent, 0, 100);
        if (Percent > 0) Muted = false;
    }

    /// <summary>Adopts an externally-observed mute state (e.g. the Windows endpoint changed
    /// outside the app) without the unmute-on-positive coupling of <see cref="SetPercent"/>.</summary>
    public void SetMuted(bool muted) => Muted = muted;

    public double CurrentDb => ToDb(Percent, Muted);

    /// <summary>The percent→preamp mapping as a pure function, for callers that hold
    /// (percent, muted) pairs without a live state (the per-device config renderer).</summary>
    public static double ToDb(int percent, bool muted) =>
        muted || percent == 0 ? MuteDb : MinDb * (100 - percent) / 99.0;
}
