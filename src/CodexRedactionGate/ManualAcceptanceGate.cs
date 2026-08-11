using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexRedactionGate;

internal sealed record ResidentOperationalReadinessProof(
    string BuildVersion,
    string CorrelationId,
    long AttemptId,
    bool Passed,
    string TerminalStatus);

internal sealed record ResidentOperationalReadinessProofResult(
    bool Available,
    string Code,
    ResidentOperationalReadinessProof? Proof);

/// <summary>
/// Stores the last successful resident local-readiness proof using only safe
/// lifecycle identifiers. It is evidence for the manual acceptance gate, not a
/// replacement for the reference or live ChatGPT acceptance records.
/// </summary>
internal static class ResidentOperationalReadinessProofStore
{
    private const string FileName = "resident-operational-readiness-proof.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    internal static string DefaultPath(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SettingsDirectory, FileName);
    }

    internal static bool TryRecord(
        DefaultStorageLayout layout,
        string buildVersion,
        string correlationId,
        long attemptId,
        bool passed,
        string terminalStatus)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var proof = new ResidentOperationalReadinessProof(
            buildVersion,
            correlationId,
            attemptId,
            passed,
            terminalStatus);
        if (!IsValid(proof))
        {
            return false;
        }

        try
        {
            layout.EnsureDirectories();
            var payload = JsonSerializer.Serialize(proof, JsonOptions) + Environment.NewLine;
            AtomicFileWriter.WriteAllBytes(DefaultPath(layout), Encoding.UTF8.GetBytes(payload));
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException)
        {
            return false;
        }
    }

    internal static bool TryClear(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        try
        {
            var path = DefaultPath(layout);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    internal static ResidentOperationalReadinessProofResult Load(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = DefaultPath(layout);
        if (!File.Exists(path))
        {
            return new ResidentOperationalReadinessProofResult(false, "resident_readiness_proof_missing", null);
        }

        try
        {
            var proof = JsonSerializer.Deserialize<ResidentOperationalReadinessProof>(
                File.ReadAllText(path),
                JsonOptions);
            return proof is not null && IsValid(proof)
                ? new ResidentOperationalReadinessProofResult(true, "resident_readiness_proof_loaded", proof)
                : new ResidentOperationalReadinessProofResult(false, "resident_readiness_proof_invalid", null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException)
        {
            return new ResidentOperationalReadinessProofResult(false, "resident_readiness_proof_unavailable", null);
        }
    }

    private static bool IsValid(ResidentOperationalReadinessProof proof)
    {
        return OperationalActionJournal.IsSafeToken(proof.BuildVersion, allowNone: false)
            && OperationalActionJournal.IsSafeToken(proof.CorrelationId, allowNone: false)
            && proof.AttemptId > 0
            && proof.Passed
            && proof.TerminalStatus == "succeeded";
    }
}

internal sealed record ManualAcceptanceGateResult(bool Allowed, string Code);

/// <summary>
/// Admission gate for the manual live ChatGPT acceptance command. It requires
/// both the resident proof and its matching raw-free terminal journal record.
/// </summary>
internal static class ManualAcceptanceGate
{
    internal static ManualAcceptanceGateResult Evaluate(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var proofResult = ResidentOperationalReadinessProofStore.Load(layout);
        if (!proofResult.Available || proofResult.Proof is null)
        {
            return new ManualAcceptanceGateResult(false, proofResult.Code);
        }

        var proof = proofResult.Proof;
        if (!string.Equals(proof.BuildVersion, BuildVersion.Current, StringComparison.Ordinal))
        {
            return new ManualAcceptanceGateResult(false, "resident_readiness_proof_build_mismatch");
        }

        IReadOnlyList<OperationalActionJournalEntry> journal;
        try
        {
            journal = OperationalActionJournal.Read(layout);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return new ManualAcceptanceGateResult(false, "operational_journal_unavailable");
        }

        var matchingTerminalRecord = journal.Any(entry =>
            entry.CorrelationId == proof.CorrelationId
            && entry.ActionKind == "local_readiness"
            && entry.Transition == "completed"
            && entry.OutcomeCode == "succeeded"
            && entry.AttemptNumber > 0
            && entry.BuildVersion == proof.BuildVersion);
        return matchingTerminalRecord
            ? new ManualAcceptanceGateResult(true, "manual_acceptance_ready")
            : new ManualAcceptanceGateResult(false, "resident_readiness_terminal_record_missing");
    }
}
