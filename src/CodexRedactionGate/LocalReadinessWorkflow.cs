using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal sealed record LocalReadinessResult(
    bool Succeeded,
    string Code,
    IReadOnlyList<ReadinessItem> Items);

internal static class LocalReadinessWorkflow
{
    public static LocalReadinessResult Run(
        DefaultStorageLayout layout,
        Func<ReadinessReport>? readinessProbe = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var report = (readinessProbe ?? (() => ReadinessDoctor.Check(layout)))();
        return new LocalReadinessResult(
            report.Ready,
            report.Ready ? "local_readiness_passed" : "local_readiness_failed",
            report.Items);
    }
}
