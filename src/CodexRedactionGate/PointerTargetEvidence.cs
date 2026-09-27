using System;
using System.Collections.Concurrent;

namespace CodexRedactionGate;

internal enum PointerTargetVerdict
{
    SelectedSend,
    Unrelated
}

/// <summary>
/// Canonical window-handle key shared by publication and resolution so a
/// non-padded handle and a padded handle address the same entry. The formatting
/// matches <see cref="NativeSubmitTargetIdentity"/> ("X" of the window-handle
/// Int64), which is how production identities carry the handle.
/// </summary>
internal static class PointerTargetEvidenceKey
{
    public static string FromHandle(IntPtr windowHandle) =>
        windowHandle.ToInt64().ToString("X");

    public static string FromHandle(string? windowHandle)
    {
        var trimmed = (windowHandle ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var digits = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..]
            : trimmed;
        return long.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var parsed)
            ? parsed.ToString("X")
            : trimmed.ToUpperInvariant();
    }
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

        _entries[PointerTargetEvidenceKey.FromHandle(target.WindowHandle)] = new Entry(target, verdict, generation);
    }

    public bool TryResolve(IntPtr capturedTargetWindow, out Entry entry) =>
        _entries.TryGetValue(PointerTargetEvidenceKey.FromHandle(capturedTargetWindow), out entry!);

    public bool TryResolve(string? windowHandle, out Entry entry) =>
        _entries.TryGetValue(PointerTargetEvidenceKey.FromHandle(windowHandle), out entry!);

    public void Invalidate(IntPtr windowHandle) =>
        _entries.TryRemove(PointerTargetEvidenceKey.FromHandle(windowHandle), out _);

    public void Invalidate(string windowHandle) =>
        _entries.TryRemove(PointerTargetEvidenceKey.FromHandle(windowHandle), out _);

    public void Clear() => _entries.Clear();
}

/// <summary>
/// One bounded pointer-target decision for the low-level callback. Only resident
/// snapshot state is consulted: the store and the selected profile carried by the
/// current snapshot. No live window, process, or UI Automation lookup happens here,
/// and a selected-client Send without fresh evidence fails closed as
/// trace_unavailable. Evidence whose snapshot generation no longer matches the
/// current resident snapshot is stale and also fails closed.
/// </summary>
internal sealed record ResidentPointerTargetDecision(
    PointerTargetVerdict Verdict,
    bool Suppressed,
    string? Status,
    NativeSubmitTargetIdentity Target,
    long EvidenceGeneration)
{
    public static ResidentPointerTargetDecision TryDecide(
        PointerTargetEvidenceStore store,
        NativePointerGesture gesture,
        ProtectionSnapshot snapshot)
    {
        var selectedProfileId = snapshot.State.ConfiguredProfileId;
        var selected = !string.IsNullOrWhiteSpace(selectedProfileId)
            && string.Equals(
                snapshot.State.LastProfileId,
                selectedProfileId,
                StringComparison.Ordinal);

        if (store.TryResolve(gesture.TargetWindow, out var entry))
        {
            var fresh = entry.Target.SnapshotGeneration == snapshot.Generation
                && entry.EvidenceGeneration <= snapshot.Generation;
            if (!fresh)
            {
                return Stale(selectedProfileId, gesture.TargetWindow);
            }

            return entry.Verdict == PointerTargetVerdict.SelectedSend
                ? new ResidentPointerTargetDecision(
                    PointerTargetVerdict.SelectedSend,
                    Suppressed: true,
                    Status: OsInteractionStatusIds.NativeSubmitGuarded,
                    Target: entry.Target,
                    EvidenceGeneration: entry.EvidenceGeneration)
                : new ResidentPointerTargetDecision(
                    PointerTargetVerdict.Unrelated,
                    Suppressed: false,
                    Status: OsInteractionStatusIds.NativeSubmitPassThrough,
                    Target: entry.Target,
                    EvidenceGeneration: entry.EvidenceGeneration);
        }

        return selected
            ? Stale(selectedProfileId!, gesture.TargetWindow)
            : new ResidentPointerTargetDecision(
                PointerTargetVerdict.Unrelated,
                Suppressed: false,
                Status: OsInteractionStatusIds.NativeSubmitPassThrough,
                Target: new NativeSubmitTargetIdentity(
                    SnapshotGeneration: snapshot.Generation,
                    ProfileId: string.Empty,
                    WindowHandle: PointerTargetEvidenceKey.FromHandle(gesture.TargetWindow)),
                EvidenceGeneration: 0);
    }

    private static ResidentPointerTargetDecision Stale(string profileId, IntPtr targetWindow) =>
        new(
            PointerTargetVerdict.SelectedSend,
            Suppressed: true,
            Status: OsInteractionStatusIds.TraceUnavailable,
            Target: new NativeSubmitTargetIdentity(
                SnapshotGeneration: 0,
                ProfileId: profileId,
                WindowHandle: PointerTargetEvidenceKey.FromHandle(targetWindow)),
            EvidenceGeneration: 0);
}
