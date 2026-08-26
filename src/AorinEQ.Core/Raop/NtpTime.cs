namespace AorinEQ.Core.Raop;

/// <summary>NTP timestamps, the format RAOP's timing and sync channels speak: 64 bits, seconds
/// since 1900-01-01 UTC in the high word, binary fraction of a second in the low word.
///
/// Pure and static so the epoch arithmetic — the part that is silently wrong by 70 years if
/// you reach for the Unix epoch out of habit — is testable without a socket.</summary>
public static class NtpTime
{
    /// <summary>1900-01-01T00:00:00Z. NOT the Unix epoch: NTP predates it by 2208988800 s.</summary>
    public static readonly DateTime Epoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const double FractionScale = 4294967296.0; // 2^32

    public static ulong FromUtc(DateTime utc)
    {
        double seconds = (utc - Epoch).TotalSeconds;
        if (seconds <= 0) return 0;
        ulong whole = (ulong)seconds;
        ulong fraction = (ulong)((seconds - whole) * FractionScale);
        return (whole << 32) | (fraction & 0xFFFFFFFF);
    }

    public static DateTime ToUtc(ulong ntp)
    {
        double seconds = (ntp >> 32) + (ntp & 0xFFFFFFFF) / FractionScale;
        return Epoch.AddSeconds(seconds);
    }

    public static ulong Now() => FromUtc(DateTime.UtcNow);
}
