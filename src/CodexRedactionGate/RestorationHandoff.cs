using System;

namespace CodexRedactionGate;

public sealed record RestorationHandoffResult(
    RestorationResult Restoration,
    RestoredOutputSubmitDecision SubmitDecision);

public static class RestorationHandoff
{
    public static RestorationHandoffResult RestoreAndEvaluate(IRestorer restorer, RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(restorer);
        ArgumentNullException.ThrowIfNull(request);

        var restoration = restorer.Restore(request);
        return new RestorationHandoffResult(
            restoration,
            RestoredOutputSubmissionGuard.Evaluate(restoration));
    }
}
