using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexRedactionGate;

public sealed record ChatGptReferenceAcceptanceProof(
    string BuildVersion,
    string FingerprintId,
    bool Passed,
    string TerminalStatus);

public sealed record ChatGptLiveContractProof(
    string BuildVersion,
    string FingerprintId,
    string SubmitBinding,
    string TerminalStatus,
    bool Passed,
    IReadOnlyList<ProtectedSendTraceEntry> Trace);

public sealed record ChatGptAcceptanceProofBundle(
    ChatGptReferenceAcceptanceProof? Reference,
    ChatGptLiveContractProof? LiveContract)
{
    public static ChatGptAcceptanceProofBundle Empty { get; } = new(null, null);
}

public sealed record ChatGptAcceptanceProofStoreResult(
    bool Succeeded,
    string Code,
    ChatGptAcceptanceProofBundle Proofs);

public sealed record ChatGptProtectedClaimResult(
    string Status,
    string ReferenceStatus,
    string LiveContractStatus,
    string Reason)
{
    public bool Protected => Status == OsInteractionStatusIds.Protected;
}

public static class ChatGptProtectedClaimEvaluator
{
    public const string DegradedStatus = "degraded";
    public const string MissingStatus = "missing";

    public static ChatGptProtectedClaimResult Evaluate(
        SubmitBindingProfile profile,
        string buildVersion,
        ChatGptAcceptanceProofBundle proofs)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildVersion);
        ArgumentNullException.ThrowIfNull(proofs);

        if (!string.Equals(profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal))
        {
            return new ChatGptProtectedClaimResult(
                OsInteractionStatusIds.Protected,
                "not_applicable",
                "not_applicable",
                "not_applicable");
        }

        if (!profile.IsProtected || profile.CompatibilityEvidence is null)
        {
            return new ChatGptProtectedClaimResult(
                OsInteractionStatusIds.SurfaceUnverified,
                MissingStatus,
                MissingStatus,
                "fingerprint_missing");
        }

        var fingerprintId = profile.CompatibilityEvidence.VerificationId;
        if (!ProtectedSendTrace.IsOpaqueFingerprint(fingerprintId))
        {
            return new ChatGptProtectedClaimResult(
                OsInteractionStatusIds.SurfaceUnverified,
                "invalid",
                "invalid",
                "fingerprint_invalid");
        }

        var referenceStatus = ReferenceStatus(proofs.Reference, buildVersion, fingerprintId);
        if (referenceStatus != "passed")
        {
            return new ChatGptProtectedClaimResult(
                DegradedStatus,
                referenceStatus,
                LiveStatus(proofs.LiveContract, buildVersion, fingerprintId, profile.SubmitBinding!.DisplayText),
                $"reference_{referenceStatus}");
        }

        var liveStatus = LiveStatus(proofs.LiveContract, buildVersion, fingerprintId, profile.SubmitBinding!.DisplayText);
        return liveStatus == "passed"
            ? new ChatGptProtectedClaimResult(
                OsInteractionStatusIds.Protected,
                referenceStatus,
                liveStatus,
                "both_proofs_match")
            : new ChatGptProtectedClaimResult(
                DegradedStatus,
                referenceStatus,
                liveStatus,
                $"live_contract_{liveStatus}");
    }

    public static ChatGptProtectedClaimResult Evaluate(
        SubmitBindingProfile profile,
        DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var stored = ChatGptAcceptanceProofStore.Load(layout);
        return Evaluate(profile, BuildVersion.Current, stored.Proofs);
    }

    public static bool TryCreateLiveProof(
        SubmitBindingProfile profile,
        string buildVersion,
        IReadOnlyList<ProtectedSendTraceEntry> trace,
        out ChatGptLiveContractProof? proof)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildVersion);
        ArgumentNullException.ThrowIfNull(trace);
        proof = null;

        if (!string.Equals(profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            || !profile.IsProtected
            || profile.CompatibilityEvidence is null
            || !ProtectedSendTrace.IsOpaqueFingerprint(profile.CompatibilityEvidence.VerificationId)
            || profile.SubmitBinding is null
            || trace.Count == 0
            || !ProtectedSendTrace.IsCompleteSafeSendTrace(trace))
        {
            return false;
        }

        proof = new ChatGptLiveContractProof(
            buildVersion,
            profile.CompatibilityEvidence.VerificationId,
            profile.SubmitBinding.DisplayText,
            "sent_safely",
            Passed: true,
            Trace: trace.ToArray());
        return true;
    }

    private static string ReferenceStatus(
        ChatGptReferenceAcceptanceProof? proof,
        string buildVersion,
        string fingerprintId)
    {
        if (proof is null)
        {
            return MissingStatus;
        }

        if (!proof.Passed || proof.TerminalStatus != "passed")
        {
            return "failed";
        }

        return proof.BuildVersion == buildVersion && proof.FingerprintId == fingerprintId
            ? "passed"
            : "mismatch";
    }

    private static string LiveStatus(
        ChatGptLiveContractProof? proof,
        string buildVersion,
        string fingerprintId,
        string submitBinding)
    {
        if (proof is null)
        {
            return MissingStatus;
        }

        if (!proof.Passed
            || proof.TerminalStatus != "sent_safely"
            || !ProtectedSendTrace.IsCompleteSafeSendTrace(proof.Trace))
        {
            return "failed";
        }

        return proof.BuildVersion == buildVersion
            && proof.FingerprintId == fingerprintId
            && proof.SubmitBinding == submitBinding
            ? "passed"
            : "mismatch";
    }

}

public static class ChatGptAcceptanceProofStore
{
    private const string FileName = "chatgpt-acceptance-proofs.json";
    private const string LiveContractArmFileName = "chatgpt-live-contract-armed.txt";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string DefaultPath(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SettingsDirectory, FileName);
    }

    public static ChatGptAcceptanceProofStoreResult Load(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = DefaultPath(layout);
        if (!File.Exists(path))
        {
            return new ChatGptAcceptanceProofStoreResult(true, "proofs_missing", ChatGptAcceptanceProofBundle.Empty);
        }

        try
        {
            var proofs = JsonSerializer.Deserialize<ChatGptAcceptanceProofBundle>(File.ReadAllText(path), JsonOptions);
            return proofs is null
                ? new ChatGptAcceptanceProofStoreResult(false, "proofs_invalid", ChatGptAcceptanceProofBundle.Empty)
                : new ChatGptAcceptanceProofStoreResult(true, "proofs_loaded", proofs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ChatGptAcceptanceProofStoreResult(false, "proofs_unavailable", ChatGptAcceptanceProofBundle.Empty);
        }
    }

    public static bool Save(DefaultStorageLayout layout, ChatGptAcceptanceProofBundle proofs)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(proofs);

        if (!IsValid(proofs))
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(proofs, JsonOptions) + Environment.NewLine;
        AtomicFileWriter.WriteAllBytes(DefaultPath(layout), Encoding.UTF8.GetBytes(payload));
        return true;
    }

    public static bool RecordReference(
        DefaultStorageLayout layout,
        SubmitBindingProfile profile,
        string buildVersion,
        bool passed,
        string terminalStatus)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalStatus);
        if (!string.Equals(profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            || profile.CompatibilityEvidence is null)
        {
            return false;
        }

        var current = Load(layout).Proofs;
        return Save(layout, current with
        {
            Reference = new ChatGptReferenceAcceptanceProof(
                buildVersion,
                profile.CompatibilityEvidence.VerificationId,
                passed,
                terminalStatus)
        });
    }

    public static bool RecordLiveContract(
        DefaultStorageLayout layout,
        SubmitBindingProfile profile,
        string buildVersion,
        IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        if (!ChatGptProtectedClaimEvaluator.TryCreateLiveProof(profile, buildVersion, trace, out var proof)
            || proof is null)
        {
            return false;
        }

        var current = Load(layout).Proofs;
        var saved = Save(layout, current with { LiveContract = proof });
        if (saved)
        {
            ClearLiveContractArm(layout);
        }

        return saved;
    }

    public static bool ArmLiveContract(DefaultStorageLayout layout, SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            || profile.CompatibilityEvidence is null
            || !ProtectedSendTrace.IsOpaqueFingerprint(profile.CompatibilityEvidence.VerificationId)
            || string.IsNullOrWhiteSpace(profile.SubmitBinding?.DisplayText))
        {
            return false;
        }

        var payload = string.Join(
            Environment.NewLine,
            $"build_version={BuildVersion.Current}",
            $"fingerprint_id={profile.CompatibilityEvidence.VerificationId}",
            "armed=true",
            string.Empty);
        AtomicFileWriter.WriteAllBytes(
            Path.Combine(layout.SettingsDirectory, LiveContractArmFileName),
            Encoding.UTF8.GetBytes(payload));
        return true;
    }

    public static bool IsLiveContractArmed(
        DefaultStorageLayout layout,
        SubmitBindingProfile profile,
        string buildVersion)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildVersion);
        if (profile.CompatibilityEvidence is null)
        {
            return false;
        }

        var path = Path.Combine(layout.SettingsDirectory, LiveContractArmFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var values = File.ReadLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            return values.TryGetValue("armed", out var armed)
                && armed == "true"
                && values.TryGetValue("build_version", out var storedBuild)
                && storedBuild == buildVersion
                && values.TryGetValue("fingerprint_id", out var storedFingerprint)
                && storedFingerprint == profile.CompatibilityEvidence.VerificationId;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ClearLiveContractArm(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = Path.Combine(layout.SettingsDirectory, LiveContractArmFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsValid(ChatGptAcceptanceProofBundle proofs)
    {
        return (proofs.Reference is null || IsValid(
            proofs.Reference.BuildVersion,
            proofs.Reference.FingerprintId,
            proofs.Reference.TerminalStatus))
            && (proofs.LiveContract is null || IsValid(
                proofs.LiveContract.BuildVersion,
                proofs.LiveContract.FingerprintId,
                proofs.LiveContract.SubmitBinding,
                proofs.LiveContract.TerminalStatus,
                proofs.LiveContract.Trace));
    }

    private static bool IsValid(string buildVersion, string fingerprintId, string status)
    {
        return IsSafeBuildVersion(buildVersion)
            && ProtectedSendTrace.IsOpaqueFingerprint(fingerprintId)
            && IsSafeStatus(status);
    }

    private static bool IsValid(
        string buildVersion,
        string fingerprintId,
        string submitBinding,
        string status,
        IReadOnlyList<ProtectedSendTraceEntry>? trace)
    {
        return IsValid(buildVersion, fingerprintId, status)
            && IsSafeBinding(submitBinding)
            && trace is not null
            && ProtectedSendTrace.IsCompleteSafeSendTrace(trace);
    }

    private static bool IsSafeBinding(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.All(character => character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '+' or '-');
    }

    private static bool IsSafeBuildVersion(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.All(character => character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '.' or '-' or '+');
    }

    private static bool IsSafeStatus(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.');
    }

}
