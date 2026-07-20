namespace CodexRedactionGate;

internal static class CliOutputFormatting
{
    public static string FormatDecision(SanitizeDecision decision)
    {
        return decision switch
        {
            SanitizeDecision.Allow => "allow",
            SanitizeDecision.Confirm => "confirm",
            SanitizeDecision.Block => "block",
            _ => decision.ToString()
        };
    }
}
