using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexRedactionGate;

public sealed record ProtectedSendTraceEntry(
    long AttemptId,
    long SnapshotGeneration,
    string TargetFingerprint,
    string Stage,
    string ResultCode,
    int DurationMilliseconds);

public sealed record ProtectedSendInterruption(
    long AttemptId,
    long SourceGeneration,
    string Reason,
    string Action);

internal enum ProtectedSendTraceStage
{
    SendDetected,
    TargetMatched,
    ComposerRead,
    Sanitized,
    OverlayDecision,
    OverlayCreated,
    OverlayForegroundConfirmed,
    Approved,
    Cancelled,
    TextWritten,
    Replayed,
    SendInjected,
    SentSafely,
    TerminalBlocked
}

internal readonly record struct ProtectedSendTraceResultCode
{
    private static readonly string[] KnownValues =
    {
        "checking_prompt",
        "target_verified",
        "capture_verified",
        "sanitization_verified",
        "confirmation_requested",
        "foreground_verified",
        "user_cancelled",
        "user_approved",
        "write_verified",
        "submit_requested",
        OsInteractionStatusIds.SupportedSurface,
        OsInteractionStatusIds.UnsupportedSurface,
        OsInteractionStatusIds.UnsupportedPlatform,
        OsInteractionStatusIds.AmbiguousSurface,
        OsInteractionStatusIds.CaptureFailed,
        OsInteractionStatusIds.WriteFailed,
        OsInteractionStatusIds.SubmitFailed,
        OsInteractionStatusIds.VerificationFailed,
        OsInteractionStatusIds.FocusLost,
        OsInteractionStatusIds.StaleComposer,
        OsInteractionStatusIds.DryRunAllow,
        OsInteractionStatusIds.DryRunConfirm,
        OsInteractionStatusIds.Blocked,
        OsInteractionStatusIds.Canceled,
        OsInteractionStatusIds.Applied,
        OsInteractionStatusIds.Submitted,
        OsInteractionStatusIds.FailedClosed,
        OsInteractionStatusIds.SafetyDisabled,
        OsInteractionStatusIds.NotComposer,
        OsInteractionStatusIds.SupportedComposer,
        OsInteractionStatusIds.EvidenceMissing,
        OsInteractionStatusIds.Protected,
        OsInteractionStatusIds.NotConfigured,
        OsInteractionStatusIds.BindingUnknown,
        OsInteractionStatusIds.SurfaceUnverified,
        OsInteractionStatusIds.DegradedHotkeyOnly,
        OsInteractionStatusIds.NativeSubmitGuarded,
        OsInteractionStatusIds.NativeSubmitInProgress,
        OsInteractionStatusIds.NativeSubmitPassThrough,
        OsInteractionStatusIds.NativeSubmitCrashed,
        OsInteractionStatusIds.TraceUnavailable,
        OsInteractionStatusIds.EmergencyDisabled,
        OsInteractionStatusIds.EnterpriseBlocked,
        OsInteractionStatusIds.NativeSubmitSetupRequired,
        OsInteractionStatusIds.ProfilesUnavailable,
        OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported,
        OsInteractionStatusIds.ReplayIndeterminate
    };

    private ProtectedSendTraceResultCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(string value, out ProtectedSendTraceResultCode result)
    {
        if (!IsSafeToken(value) || Array.IndexOf(KnownValues, value) < 0)
        {
            result = default;
            return false;
        }

        result = new ProtectedSendTraceResultCode(value);
        return true;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(Value)
        && IsSafeToken(Value)
        && IsKnown(Value);

    public static bool IsKnown(string value)
        => Array.IndexOf(KnownValues, value) >= 0;

    private static bool IsSafeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct ProtectedSendTraceTransition(
    ProtectedSendTraceStage Stage,
    ProtectedSendTraceResultCode ResultCode)
{
    private static readonly IReadOnlyDictionary<ProtectedSendTraceStage, string> StageTokens =
        new Dictionary<ProtectedSendTraceStage, string>
        {
            [ProtectedSendTraceStage.SendDetected] = "send_detected",
            [ProtectedSendTraceStage.TargetMatched] = "target_matched",
            [ProtectedSendTraceStage.ComposerRead] = "composer_read",
            [ProtectedSendTraceStage.Sanitized] = "sanitized",
            [ProtectedSendTraceStage.OverlayDecision] = "overlay_decision",
            [ProtectedSendTraceStage.OverlayCreated] = "overlay_created",
            [ProtectedSendTraceStage.OverlayForegroundConfirmed] = "overlay_foreground_confirmed",
            [ProtectedSendTraceStage.Approved] = "approved",
            [ProtectedSendTraceStage.Cancelled] = "cancelled",
            [ProtectedSendTraceStage.TextWritten] = "text_written",
            [ProtectedSendTraceStage.Replayed] = "replayed",
            [ProtectedSendTraceStage.SendInjected] = "send_injected",
            [ProtectedSendTraceStage.SentSafely] = "sent_safely",
            [ProtectedSendTraceStage.TerminalBlocked] = "terminal_blocked"
        };

    public static bool TryCreate(
        string stage,
        string resultCode,
        out ProtectedSendTraceTransition transition)
    {
        if (!TryParseStage(stage, out var parsedStage)
            || !ProtectedSendTraceResultCode.TryCreate(resultCode, out var parsedResultCode))
        {
            transition = default;
            return false;
        }

        transition = new ProtectedSendTraceTransition(parsedStage, parsedResultCode);
        return transition.IsValid;
    }

    public bool IsValid => StageTokens.ContainsKey(Stage)
        && ResultCode.IsValid
        && IsAllowedResultCode(Stage, ResultCode.Value);

    public static bool TryParseStageToken(string value, out ProtectedSendTraceStage stage)
    {
        foreach (var pair in StageTokens)
        {
            if (string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                stage = pair.Key;
                return true;
            }
        }

        stage = default;
        return false;
    }

    public string StageToken => StageTokens.TryGetValue(Stage, out var token) ? token : "unavailable";

    internal static string CanonicalizeAdapterStage(string stage) => stage switch
    {
        "overlay_created" => "overlay_decision",
        "send_injected" => "replayed",
        _ => stage
    };

    internal static string ObserverStage(string canonicalStage) => canonicalStage switch
    {
        "overlay_decision" or "overlay_foreground_confirmed" or "approved" or "cancelled" => "overlay",
        "text_written" => "write",
        "replayed" => "replay",
        _ => canonicalStage
    };

    private static bool IsAllowedResultCode(
        ProtectedSendTraceStage stage,
        string resultCode)
    {
        return stage switch
        {
            ProtectedSendTraceStage.SendDetected => resultCode == "checking_prompt",
            ProtectedSendTraceStage.TargetMatched => resultCode == "target_verified",
            ProtectedSendTraceStage.ComposerRead => resultCode == "capture_verified",
            ProtectedSendTraceStage.Sanitized => resultCode == "sanitization_verified",
            ProtectedSendTraceStage.OverlayDecision => resultCode is "confirmation_requested" or OsInteractionStatusIds.DryRunAllow,
            ProtectedSendTraceStage.OverlayCreated => resultCode == "confirmation_requested",
            ProtectedSendTraceStage.OverlayForegroundConfirmed => resultCode == "foreground_verified",
            ProtectedSendTraceStage.Approved => resultCode == "user_approved",
            ProtectedSendTraceStage.Cancelled => resultCode == "user_cancelled",
            ProtectedSendTraceStage.TextWritten => resultCode == "write_verified",
            ProtectedSendTraceStage.Replayed => resultCode == "submit_requested",
            ProtectedSendTraceStage.SendInjected => resultCode == "submit_requested",
            ProtectedSendTraceStage.SentSafely => resultCode == OsInteractionStatusIds.Submitted,
            ProtectedSendTraceStage.TerminalBlocked => IsBlockedResultCode(resultCode),
            _ => false
        };
    }

    private static bool IsBlockedResultCode(string resultCode)
    {
        return ProtectedSendTraceResultCode.IsKnown(resultCode)
            && resultCode is not (
                OsInteractionStatusIds.Applied
                or OsInteractionStatusIds.Protected
                or OsInteractionStatusIds.Submitted
                or OsInteractionStatusIds.NativeSubmitGuarded
                or OsInteractionStatusIds.NativeSubmitInProgress
                or OsInteractionStatusIds.NativeSubmitPassThrough
                or OsInteractionStatusIds.DryRunAllow
                or OsInteractionStatusIds.DryRunConfirm);
    }

    private static bool TryParseStage(string value, out ProtectedSendTraceStage stage)
        => TryParseStageToken(value, out stage);
}

internal static class ProtectedSendTrace
{
    public static string TargetFingerprint(NativeSubmitTargetIdentity? target, string? profileId = null)
    {
        var identity = target is null
            ? $"{profileId ?? "unavailable"}|unavailable"
            : $"{target.ProfileId}|{target.WindowHandle}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static bool TryAppend(
        IReadOnlyList<ProtectedSendTraceEntry> current,
        long attemptId,
        long snapshotGeneration,
        string targetFingerprint,
        string stage,
        string resultCode,
        int durationMilliseconds,
        out IReadOnlyList<ProtectedSendTraceEntry> updated)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!ProtectedSendTraceTransition.TryCreate(stage, resultCode, out var transition))
        {
            updated = current;
            return false;
        }

        return TryAppend(
            current,
            attemptId,
            snapshotGeneration,
            targetFingerprint,
            transition,
            durationMilliseconds,
            out updated);
    }

    public static bool TryAppend(
        IReadOnlyList<ProtectedSendTraceEntry> current,
        long attemptId,
        long snapshotGeneration,
        string targetFingerprint,
        ProtectedSendTraceTransition transition,
        int durationMilliseconds,
        out IReadOnlyList<ProtectedSendTraceEntry> updated)
    {
        ArgumentNullException.ThrowIfNull(current);

        updated = current;
        if (string.IsNullOrWhiteSpace(targetFingerprint)
            || attemptId <= 0
            || snapshotGeneration < 0
            || current.Count > 32
            || durationMilliseconds < 0
            || !IsOpaqueFingerprint(targetFingerprint)
            || !transition.IsValid)
        {
            return false;
        }

        if (transition.Stage == ProtectedSendTraceStage.SendDetected)
        {
            if (current.Count != 0)
            {
                return false;
            }
        }
        else if (current.Count == 0
            || current[^1].AttemptId != attemptId
            || current[^1].SnapshotGeneration != snapshotGeneration
            || current[^1].TargetFingerprint != targetFingerprint
            || !TryParseStage(current[^1].Stage, out var previousStage)
            || !IsAllowed(previousStage, transition.Stage))
        {
            return false;
        }

        var entries = new List<ProtectedSendTraceEntry>(current)
        {
            new(
                attemptId,
                snapshotGeneration,
                targetFingerprint,
                transition.StageToken,
                transition.ResultCode.Value,
                durationMilliseconds)
        };
        updated = entries;
        return true;
    }

    private static bool TryParseStage(string value, out ProtectedSendTraceStage stage)
        => ProtectedSendTraceTransition.TryParseStageToken(value, out stage);

    private static bool IsAllowed(ProtectedSendTraceStage previous, ProtectedSendTraceStage next)
    {
        return previous switch
        {
            ProtectedSendTraceStage.SendDetected => next is ProtectedSendTraceStage.TargetMatched or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.TargetMatched => next is ProtectedSendTraceStage.ComposerRead or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.ComposerRead => next is ProtectedSendTraceStage.Sanitized or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Sanitized => next is ProtectedSendTraceStage.OverlayDecision
                or ProtectedSendTraceStage.OverlayCreated
                or ProtectedSendTraceStage.Replayed
                or ProtectedSendTraceStage.SendInjected
                or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.OverlayDecision => next is ProtectedSendTraceStage.OverlayForegroundConfirmed
                or ProtectedSendTraceStage.Approved
                or ProtectedSendTraceStage.Cancelled
                or ProtectedSendTraceStage.Replayed
                or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.OverlayCreated => next is ProtectedSendTraceStage.OverlayForegroundConfirmed or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.OverlayForegroundConfirmed => next is ProtectedSendTraceStage.Approved or ProtectedSendTraceStage.Cancelled or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Cancelled => next is ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Approved => next is ProtectedSendTraceStage.TextWritten or ProtectedSendTraceStage.SendInjected or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.TextWritten => next is ProtectedSendTraceStage.Replayed
                or ProtectedSendTraceStage.SendInjected
                or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Replayed => next is ProtectedSendTraceStage.SentSafely or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.SendInjected => next is ProtectedSendTraceStage.SentSafely or ProtectedSendTraceStage.TerminalBlocked,
            _ => false
        };
    }

    internal static bool IsOpaqueFingerprint(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidTerminalTrace(IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        if (trace is null || trace.Count < 5 || trace.Any(entry => entry is null))
        {
            return false;
        }

        IReadOnlyList<ProtectedSendTraceEntry> rebuilt = Array.Empty<ProtectedSendTraceEntry>();
        foreach (var entry in trace)
        {
            if (!TryAppend(
                    rebuilt,
                    entry.AttemptId,
                    entry.SnapshotGeneration,
                    entry.TargetFingerprint,
                    entry.Stage,
                    entry.ResultCode,
                    entry.DurationMilliseconds,
                    out rebuilt))
            {
                return false;
            }
        }

        return rebuilt[0].Stage == "send_detected"
            && rebuilt[1].Stage == "target_matched"
            && rebuilt[2].Stage == "composer_read"
            && rebuilt[3].Stage == "sanitized"
            && rebuilt[^1].Stage is "sent_safely" or "terminal_blocked";
    }

    internal static bool IsCompleteSafeSendTrace(IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        if (!IsValidTerminalTrace(trace) || trace[^1].Stage != "sent_safely")
        {
            return false;
        }

        var stages = trace.Select(entry => entry.Stage).ToArray();
        return stages.SequenceEqual(new[]
                {
                    "send_detected",
                    "target_matched",
                    "composer_read",
                    "sanitized",
                    "overlay_decision",
                    "replayed",
                    "sent_safely"
                })
            || stages.SequenceEqual(new[]
                {
                    "send_detected",
                    "target_matched",
                    "composer_read",
                    "sanitized",
                    "send_injected",
                    "sent_safely"
                })
            || stages.SequenceEqual(new[]
                {
                    "send_detected",
                    "target_matched",
                    "composer_read",
                    "sanitized",
                    "overlay_created",
                    "overlay_foreground_confirmed",
                    "approved",
                    "text_written",
                    "send_injected",
                    "sent_safely"
                })
            || stages.SequenceEqual(new[]
                {
                    "send_detected",
                    "target_matched",
                    "composer_read",
                    "sanitized",
                    "overlay_decision",
                    "overlay_foreground_confirmed",
                    "approved",
                    "text_written",
                    "replayed",
                    "sent_safely"
                });
    }

}
