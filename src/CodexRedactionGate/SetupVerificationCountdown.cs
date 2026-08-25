using System;

namespace CodexRedactionGate;

internal sealed class SetupVerificationCountdown
{
    public static TimeSpan DefaultDelay { get; } = TimeSpan.FromSeconds(10);

    public SetupVerificationCountdown(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        TotalSeconds = (int)Math.Ceiling(delay.TotalSeconds);
        RemainingSeconds = TotalSeconds;
    }

    public int TotalSeconds { get; }

    public int RemainingSeconds { get; private set; }

    public int Start()
    {
        RemainingSeconds = TotalSeconds;
        return RemainingSeconds;
    }

    public int Tick()
    {
        if (RemainingSeconds > 0)
        {
            RemainingSeconds--;
        }

        return RemainingSeconds;
    }
}
