using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

internal sealed class SanitizerDecisionTrace
{
    private readonly Dictionary<string, string> _metadata = new()
    {
        ["trace.stage.attachments"] = "not_checked",
        ["trace.stage.policy"] = "not_checked",
        ["trace.stage.scanner"] = "not_run",
        ["trace.stage.detectors"] = "not_run",
        ["trace.stage.verification"] = "not_run"
    };

    public void MarkAttachments(string status)
    {
        Set("trace.stage.attachments", status);
    }

    public void MarkPolicy(string status)
    {
        Set("trace.stage.policy", status);
    }

    public void MarkScanner(string status)
    {
        Set("trace.stage.scanner", status);
    }

    public void MarkDetectors(string status)
    {
        Set("trace.stage.detectors", status);
    }

    public void MarkVerification(string status)
    {
        Set("trace.stage.verification", status);
    }

    public void SetReason(string reasonCode)
    {
        Set("trace.reason", reasonCode);
    }

    public void AddCandidateCounts(IReadOnlyList<SensitiveCandidate> candidates)
    {
        Set("trace.count.candidates", candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var group in candidates.GroupBy(candidate => candidate.Type.Value))
        {
            Set($"trace.type.{group.Key}", group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var group in candidates.GroupBy(candidate => candidate.Action.Value))
        {
            Set($"trace.action.{group.Key}", group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var group in candidates.GroupBy(candidate => candidate.DetectorId.Value))
        {
            Set($"trace.detector.{group.Key}", group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public IReadOnlyDictionary<string, string> ToScannerStatuses(IReadOnlyDictionary<string, string>? scannerStatuses = null)
    {
        var merged = new Dictionary<string, string>(_metadata);

        if (scannerStatuses is not null)
        {
            foreach (var item in scannerStatuses)
            {
                merged[item.Key] = item.Value;
            }
        }

        return merged;
    }

    private void Set(string key, string value)
    {
        _metadata[key] = value;
    }
}
