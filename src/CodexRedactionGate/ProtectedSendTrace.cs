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

        updated = current;
        if (string.IsNullOrWhiteSpace(targetFingerprint)
            || string.IsNullOrWhiteSpace(stage)
            || string.IsNullOrWhiteSpace(resultCode)
            || attemptId <= 0
            || snapshotGeneration < 0
            || current.Count > 32
            || durationMilliseconds < 0
            || !IsHexFingerprint(targetFingerprint)
            || !IsSafeToken(resultCode))
        {
            return false;
        }

        if (stage == "send_detected")
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
            || !IsAllowed(current[^1].Stage, stage))
        {
            return false;
        }

        var entries = new List<ProtectedSendTraceEntry>(current)
        {
            new(attemptId, snapshotGeneration, targetFingerprint, stage, resultCode, durationMilliseconds)
        };
        updated = entries;
        return true;
    }

    private static bool IsAllowed(string previous, string next)
    {
        return previous switch
        {
            "send_detected" => next is "target_matched" or "terminal_blocked",
            "target_matched" => next is "composer_read" or "terminal_blocked",
            "composer_read" => next is "sanitized" or "terminal_blocked",
            "sanitized" => next is "overlay_created" or "send_injected" or "terminal_blocked",
            "overlay_created" => next is "overlay_foreground_confirmed" or "terminal_blocked",
            "overlay_foreground_confirmed" => next is "approved" or "cancelled" or "terminal_blocked",
            "cancelled" => next is "terminal_blocked",
            "approved" => next is "text_written" or "send_injected" or "terminal_blocked",
            "text_written" => next is "send_injected" or "terminal_blocked",
            "send_injected" => next is "sent_safely" or "terminal_blocked",
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

    private static bool IsSafeToken(string value)
    {
        if (value.Length is 0 or > 64)
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
