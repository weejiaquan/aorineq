namespace ApoVolume.Core;

/// <summary>Pure volume model: Windows-style percent mapped linearly in dB to an APO preamp value.</summary>
public sealed class VolumeState
{
    public const int StepPercent = 2;
    public const double MuteDb = -120.0;
    public const double MinDb = -50.0;

    public int Percent { get; private set; }
    public bool Muted { get; private set; }

    public VolumeState(int percent = 50, bool muted = false)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Muted = muted;
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

    public double CurrentDb =>
        Muted || Percent == 0 ? MuteDb : MinDb * (100 - Percent) / 99.0;
}
