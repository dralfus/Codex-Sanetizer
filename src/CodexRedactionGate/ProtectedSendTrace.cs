using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    OverlayCreated,
    OverlayForegroundConfirmed,
    Approved,
    Cancelled,
    TextWritten,
    SendInjected,
    SentSafely,
    TerminalBlocked
}

internal readonly record struct ProtectedSendTraceResultCode
{
    private ProtectedSendTraceResultCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(string value, out ProtectedSendTraceResultCode result)
    {
        if (!IsSafeToken(value))
        {
            result = default;
            return false;
        }

        result = new ProtectedSendTraceResultCode(value);
        return true;
    }

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
        return true;
    }

    public static bool TryParseStageToken(string value, out ProtectedSendTraceStage stage)
    {
        stage = value switch
        {
            "send_detected" => ProtectedSendTraceStage.SendDetected,
            "target_matched" => ProtectedSendTraceStage.TargetMatched,
            "composer_read" => ProtectedSendTraceStage.ComposerRead,
            "sanitized" => ProtectedSendTraceStage.Sanitized,
            "overlay_created" => ProtectedSendTraceStage.OverlayCreated,
            "overlay_foreground_confirmed" => ProtectedSendTraceStage.OverlayForegroundConfirmed,
            "approved" => ProtectedSendTraceStage.Approved,
            "cancelled" => ProtectedSendTraceStage.Cancelled,
            "text_written" => ProtectedSendTraceStage.TextWritten,
            "send_injected" => ProtectedSendTraceStage.SendInjected,
            "sent_safely" => ProtectedSendTraceStage.SentSafely,
            "terminal_blocked" => ProtectedSendTraceStage.TerminalBlocked,
            _ => default
        };

        return value is "send_detected" or "target_matched" or "composer_read"
            or "sanitized" or "overlay_created" or "overlay_foreground_confirmed"
            or "approved" or "cancelled" or "text_written" or "send_injected"
            or "sent_safely" or "terminal_blocked";
    }

    public string StageToken => Stage switch
    {
        ProtectedSendTraceStage.SendDetected => "send_detected",
        ProtectedSendTraceStage.TargetMatched => "target_matched",
        ProtectedSendTraceStage.ComposerRead => "composer_read",
        ProtectedSendTraceStage.Sanitized => "sanitized",
        ProtectedSendTraceStage.OverlayCreated => "overlay_created",
        ProtectedSendTraceStage.OverlayForegroundConfirmed => "overlay_foreground_confirmed",
        ProtectedSendTraceStage.Approved => "approved",
        ProtectedSendTraceStage.Cancelled => "cancelled",
        ProtectedSendTraceStage.TextWritten => "text_written",
        ProtectedSendTraceStage.SendInjected => "send_injected",
        ProtectedSendTraceStage.SentSafely => "sent_safely",
        ProtectedSendTraceStage.TerminalBlocked => "terminal_blocked",
        _ => "unavailable"
    };

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
            || !IsHexFingerprint(targetFingerprint))
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
            ProtectedSendTraceStage.Sanitized => next is ProtectedSendTraceStage.OverlayCreated or ProtectedSendTraceStage.SendInjected or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.OverlayCreated => next is ProtectedSendTraceStage.OverlayForegroundConfirmed or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.OverlayForegroundConfirmed => next is ProtectedSendTraceStage.Approved or ProtectedSendTraceStage.Cancelled or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Cancelled => next is ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.Approved => next is ProtectedSendTraceStage.TextWritten or ProtectedSendTraceStage.SendInjected or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.TextWritten => next is ProtectedSendTraceStage.SendInjected or ProtectedSendTraceStage.TerminalBlocked,
            ProtectedSendTraceStage.SendInjected => next is ProtectedSendTraceStage.SentSafely or ProtectedSendTraceStage.TerminalBlocked,
            _ => false
        };
    }

    private static bool IsHexFingerprint(string value)
    {
        if (value.Length != 64)
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

}
