using System;
using System.Collections.Concurrent;

namespace CodexRedactionGate;

internal enum PointerTargetVerdict
{
    SelectedSend,
    Unrelated
}

/// <summary>
/// Resident, precomputed pointer-target evidence (ticket 314). Published
/// out-of-band when target discovery runs; the low-level callback resolves
/// only by bounded lookup, so it never waits for live UI Automation.
/// </summary>
internal sealed class PointerTargetEvidenceStore
{
    internal sealed record Entry(
        NativeSubmitTargetIdentity Target,
        PointerTargetVerdict Verdict,
        long EvidenceGeneration);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Publish(NativeSubmitTargetIdentity target, PointerTargetVerdict verdict, long generation)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        _entries[Normalize(target.WindowHandle)] = new Entry(target, verdict, generation);
    }

    public bool TryResolve(IntPtr capturedTargetWindow, out Entry entry)
    {
        return _entries.TryGetValue(Normalize(capturedTargetWindow), out entry!);
    }

    public void Invalidate(string windowHandle)
    {
        _entries.TryRemove(Normalize(windowHandle), out _);
    }

    public void Clear()
    {
        _entries.Clear();
    }

    private static string Normalize(IntPtr windowHandle) =>
        windowHandle.ToInt64().ToString("X16");

    private static string Normalize(string windowHandle) =>
        long.TryParse(windowHandle, System.Globalization.NumberStyles.HexNumber, null, out var parsed)
            ? parsed.ToString("X16")
            : windowHandle.Trim().ToUpperInvariant();
}

/// <summary>
/// One bounded pointer-target decision for the low-level callback. When fresh
/// resident evidence exists it is the only source consulted; a selected-client
/// Send without evidence fails closed as trace_unavailable. Live UI Automation
/// is never invoked from this path.
/// </summary>
internal sealed record ResidentPointerTargetDecision(
    PointerTargetVerdict Verdict,
    bool Suppressed,
    string? Status,
    NativeSubmitTargetIdentity Target,
    long EvidenceGeneration)
{
    public static ResidentPointerTargetDecision? TryDecide(
        PointerTargetEvidenceStore store,
        ISendControlDiscovery unusedDiscovery,
        NativePointerGesture gesture,
        Func<IntPtr, string?>? selectedProfileIdResolver = null)
    {
        if (store.TryResolve(gesture.TargetWindow, out var entry))
        {
            if (entry.Verdict == PointerTargetVerdict.SelectedSend)
            {
                return new ResidentPointerTargetDecision(
                    PointerTargetVerdict.SelectedSend,
                    Suppressed: true,
                    Status: OsInteractionStatusIds.NativeSubmitGuarded,
                    Target: entry.Target,
                    EvidenceGeneration: entry.EvidenceGeneration);
            }

            return new ResidentPointerTargetDecision(
                PointerTargetVerdict.Unrelated,
                Suppressed: false,
                Status: OsInteractionStatusIds.NativeSubmitPassThrough,
                Target: entry.Target,
                EvidenceGeneration: entry.EvidenceGeneration);
        }

        var profileId = selectedProfileIdResolver?.Invoke(gesture.TargetWindow);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return new ResidentPointerTargetDecision(
                PointerTargetVerdict.Unrelated,
                Suppressed: false,
                Status: OsInteractionStatusIds.NativeSubmitPassThrough,
                Target: new NativeSubmitTargetIdentity(
                    SnapshotGeneration: 0,
                    ProfileId: string.Empty,
                    WindowHandle: gesture.TargetWindow.ToInt64().ToString("X16")),
                EvidenceGeneration: 0);
        }

        return new ResidentPointerTargetDecision(
            PointerTargetVerdict.SelectedSend,
            Suppressed: true,
            Status: OsInteractionStatusIds.TraceUnavailable,
            Target: new NativeSubmitTargetIdentity(
                SnapshotGeneration: 0,
                ProfileId: profileId,
                WindowHandle: gesture.TargetWindow.ToInt64().ToString("X16")),
            EvidenceGeneration: 0);
    }
}
