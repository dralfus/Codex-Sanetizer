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
        uint TargetProcessId,
        PointerTargetVerdict Verdict,
        long EvidenceGeneration);

    private readonly record struct ControlKey(uint ProcessId, int X, int Y, string Button)
    {
        internal static ControlKey FromGesture(NativePointerGesture gesture) =>
            new(gesture.TargetProcessId, gesture.X, gesture.Y, gesture.Button.ToUpperInvariant());
    }

    // A verified reference point is evidence for that point only. It cannot
    // authorize another control in the same composer/root window. Production
    // control geometry and layout invalidation remain part of ticket 356.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ControlKey, Entry>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _residentWindows = new(StringComparer.Ordinal);

    public void Publish(
        NativeSubmitTargetIdentity target,
        NativePointerGesture verifiedGesture,
        PointerTargetVerdict verdict,
        long generation)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (verifiedGesture.TargetWindow == IntPtr.Zero || verifiedGesture.TargetProcessId == 0)
        {
            return;
        }

        var windowKey = PointerTargetEvidenceKey.FromHandle(verifiedGesture.TargetWindow);
        _residentWindows[windowKey] = 0;
        var controls = _entries.GetOrAdd(windowKey,
            _ => new ConcurrentDictionary<ControlKey, Entry>());
        controls[ControlKey.FromGesture(verifiedGesture)] =
            new Entry(target, verifiedGesture.TargetProcessId, verdict, generation);
    }

    public bool HasWindowEvidence(IntPtr window) =>
        _residentWindows.ContainsKey(PointerTargetEvidenceKey.FromHandle(window));

    public bool TryResolve(NativePointerGesture gesture, out Entry entry)
    {
        entry = null!;
        return _entries.TryGetValue(PointerTargetEvidenceKey.FromHandle(gesture.TargetWindow), out var controls)
            && controls.TryGetValue(ControlKey.FromGesture(gesture), out entry!);
    }

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
/// and a previously identified selected-client Send with stale evidence fails closed as
/// trace_unavailable. Unknown points pass through. Evidence whose snapshot generation no longer matches the
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
        if (store.TryResolve(gesture, out var entry))
        {
            var selected = snapshot.RuntimeSet is { } runtimes
                ? System.Linq.Enumerable.Any(runtimes.Runtimes, runtime =>
                    string.Equals(runtime.Profile.ProfileId, entry.Target.ProfileId, StringComparison.Ordinal))
                : string.Equals(snapshot.State.ConfiguredProfileId, entry.Target.ProfileId, StringComparison.Ordinal);
            if (!selected || entry.Verdict == PointerTargetVerdict.Unrelated)
            {
                return Unrelated(snapshot, gesture);
            }

            var fresh = entry.Target.SnapshotGeneration == snapshot.Generation
                && entry.EvidenceGeneration == snapshot.Generation;
            if (!fresh)
            {
                return Stale(entry.Target.ProfileId, gesture.TargetWindow, snapshot.Generation);
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

        // No evidence for this exact control. SPEC: input the callback cannot
        // identify stays outside the protected boundary — ordinary clicks,
        // copy/paste, navigation, and non-Send controls retain normal behavior.
        // The callback never classifies or suppresses an unidentified click, so
        // an unrelated gesture can never be consumed for lacking evidence.
        return Unrelated(snapshot, gesture);
    }

    private static ResidentPointerTargetDecision Unrelated(
        ProtectionSnapshot snapshot,
        NativePointerGesture gesture) =>
        new(
            PointerTargetVerdict.Unrelated,
            Suppressed: false,
            Status: OsInteractionStatusIds.NativeSubmitPassThrough,
            Target: new NativeSubmitTargetIdentity(
                SnapshotGeneration: snapshot.Generation,
                ProfileId: string.Empty,
                WindowHandle: PointerTargetEvidenceKey.FromHandle(gesture.TargetWindow)),
            EvidenceGeneration: 0);

    private static ResidentPointerTargetDecision Stale(
        string profileId, IntPtr targetWindow, long snapshotGeneration) =>
        new(
            PointerTargetVerdict.SelectedSend,
            Suppressed: true,
            Status: OsInteractionStatusIds.TraceUnavailable,
            Target: new NativeSubmitTargetIdentity(
                SnapshotGeneration: snapshotGeneration,
                ProfileId: profileId,
                WindowHandle: PointerTargetEvidenceKey.FromHandle(targetWindow)),
            EvidenceGeneration: 0);
}
