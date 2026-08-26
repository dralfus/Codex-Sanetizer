using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

public static class DevelopmentEvidenceContract
{
    public const string SchemaVersion = "1";
}

public enum DevelopmentEvidenceState
{
    Proposed,
    ReproducedRed,
    Implemented,
    LocallyVerified,
    LiveVerified,
    Released
}

public static class DevelopmentEvidenceStateTokens
{
    private static readonly IReadOnlyDictionary<DevelopmentEvidenceState, string> Tokens =
        new Dictionary<DevelopmentEvidenceState, string>
        {
            [DevelopmentEvidenceState.Proposed] = "proposed",
            [DevelopmentEvidenceState.ReproducedRed] = "reproduced_red",
            [DevelopmentEvidenceState.Implemented] = "implemented",
            [DevelopmentEvidenceState.LocallyVerified] = "locally_verified",
            [DevelopmentEvidenceState.LiveVerified] = "live_verified",
            [DevelopmentEvidenceState.Released] = "released"
        };

    public static string ToToken(DevelopmentEvidenceState state)
        => Tokens.TryGetValue(state, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(nameof(state));

    public static bool TryParse(string? token, out DevelopmentEvidenceState state)
    {
        foreach (var pair in Tokens)
        {
            if (string.Equals(pair.Value, token, StringComparison.Ordinal))
            {
                state = pair.Key;
                return true;
            }
        }

        state = default;
        return false;
    }

    public static bool TryAdvance(
        DevelopmentEvidenceState current,
        DevelopmentEvidenceState next)
    {
        return Enum.IsDefined(current)
            && Enum.IsDefined(next)
            && (int)next == (int)current + 1;
    }
}

public sealed record ProtectedSendEvidenceRecord(
    string SchemaVersion,
    string TicketId,
    string BehaviorId,
    DevelopmentEvidenceState State,
    string ReproductionId,
    string ReproductionCommandId,
    string HighestRequiredSeam,
    string BuildVersion,
    string SourceCommit,
    string ExecutableSha256,
    string InstallerIdentity,
    string CompatibilityFingerprint,
    string SubmitBinding,
    IReadOnlyList<DevelopmentEvidenceState> TransitionHistory,
    string Claim = "unverified");

public sealed record ProtectedSendEvidenceBinding(
    string SchemaVersion,
    string BuildVersion,
    string SourceCommit,
    string ExecutableSha256,
    string InstallerIdentity,
    string CompatibilityFingerprint,
    string SubmitBinding);

public sealed record EvidenceValidationResult(bool Valid, string Code);

public static class ProtectedSendEvidenceValidator
{
    private const string Unbound = "unbound";
    private const string FixedClaim = "fixed";
    private const string ReleasedClaim = "released";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static EvidenceValidationResult Validate(
        ProtectedSendEvidenceRecord record,
        string expectedBuildVersion,
        string expectedSourceCommit,
        DevelopmentEvidenceState minimumState)
        => Validate(
            record,
            new ProtectedSendEvidenceBinding(
                DevelopmentEvidenceContract.SchemaVersion,
                expectedBuildVersion,
                expectedSourceCommit,
                Unbound,
                Unbound,
                Unbound,
                Unbound),
            minimumState);

    public static EvidenceValidationResult Validate(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding expected,
        DevelopmentEvidenceState minimumState)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(expected);

        if (!Enum.IsDefined(record.State))
        {
            return Invalid("invalid_evidence_state");
        }

        if (!Enum.IsDefined(minimumState))
        {
            return Invalid("invalid_required_evidence_state");
        }

        if (!IsSafeIdentifier(expected.SchemaVersion)
            || expected.SchemaVersion != DevelopmentEvidenceContract.SchemaVersion)
        {
            return Invalid("unsupported_schema_version");
        }

        if (!IsBoundBuildVersion(expected.BuildVersion))
        {
            return Invalid("invalid_expected_build_version");
        }

        if (!IsSafeCommit(expected.SourceCommit)
            || !IsSafeHash(expected.ExecutableSha256)
            || !IsSafeBuildVersion(expected.InstallerIdentity)
            || !IsSafeHash(expected.CompatibilityFingerprint)
            || !IsSafeIdentifier(expected.SubmitBinding))
        {
            return Invalid("invalid_expected_binding");
        }

        if (!string.Equals(record.SchemaVersion, expected.SchemaVersion, StringComparison.Ordinal)
            || !IsSafeIdentifier(record.SchemaVersion))
        {
            return Invalid("invalid_schema_version");
        }

        if (!IsSafeIdentifier(record.TicketId))
        {
            return Invalid("invalid_ticket_id");
        }

        if (!IsSafeIdentifier(record.BehaviorId))
        {
            return Invalid("invalid_behavior_id");
        }

        if (!IsSafeIdentifier(record.ReproductionId))
        {
            return Invalid("invalid_reproduction_id");
        }

        if (!IsSafeIdentifier(record.ReproductionCommandId))
        {
            return Invalid("invalid_reproduction_command");
        }

        if (!IsSafeIdentifier(record.HighestRequiredSeam))
        {
            return Invalid("invalid_highest_required_seam");
        }

        if (!IsBoundBuildVersion(record.BuildVersion))
        {
            return Invalid("invalid_build_version");
        }

        if (!IsSafeCommit(record.SourceCommit))
        {
            return Invalid("invalid_source_commit");
        }

        if (!IsSafeHash(record.ExecutableSha256))
        {
            return Invalid("invalid_executable_hash");
        }

        if (!IsSafeBuildVersion(record.InstallerIdentity))
        {
            return Invalid("invalid_installer_identity");
        }

        if (!IsSafeHash(record.CompatibilityFingerprint))
        {
            return Invalid("invalid_compatibility_fingerprint");
        }

        if (!IsSafeIdentifier(record.SubmitBinding))
        {
            return Invalid("invalid_submit_binding");
        }

        if (!IsSafeClaim(record.Claim))
        {
            return Invalid("invalid_evidence_claim");
        }

        if ((int)record.State < (int)minimumState)
        {
            return Invalid("evidence_state_below_required");
        }

        if (!string.Equals(record.BuildVersion, expected.BuildVersion, StringComparison.Ordinal))
        {
            return Invalid("evidence_build_mismatch");
        }

        if (!string.Equals(record.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
            && expected.SourceCommit != Unbound)
        {
            return Invalid("evidence_source_mismatch");
        }

        if (!HasValidHistory(record))
        {
            return Invalid("invalid_evidence_history");
        }

        if ((int)record.State >= (int)DevelopmentEvidenceState.LiveVerified)
        {
            if (record.SourceCommit == Unbound
                || record.ExecutableSha256 == Unbound
                || record.InstallerIdentity == Unbound
                || record.CompatibilityFingerprint == Unbound
                || record.SubmitBinding == Unbound
                || expected.SourceCommit == Unbound
                || expected.ExecutableSha256 == Unbound
                || expected.InstallerIdentity == Unbound
                || expected.CompatibilityFingerprint == Unbound
                || expected.SubmitBinding == Unbound)
            {
                return Invalid("expected_binding_incomplete");
            }
        }

        var artifactMismatch = CompareExpectedArtifacts(record, expected);
        if (artifactMismatch is not null)
        {
            return Invalid(artifactMismatch);
        }

        if (record.Claim == FixedClaim
            && (int)record.State < (int)DevelopmentEvidenceState.LiveVerified)
        {
            return Invalid("fixed_claim_requires_live_verified");
        }

        if (record.Claim == ReleasedClaim
            && record.State != DevelopmentEvidenceState.Released)
        {
            return Invalid("released_claim_requires_released_state");
        }

        return new EvidenceValidationResult(true, "valid");
    }

    public static string Serialize(ProtectedSendEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var validation = Validate(
            record,
            new ProtectedSendEvidenceBinding(
                record.SchemaVersion,
                record.BuildVersion,
                record.SourceCommit,
                record.ExecutableSha256,
                record.InstallerIdentity,
                record.CompatibilityFingerprint,
                record.SubmitBinding),
            record.State);
        if (!validation.Valid)
        {
            throw new InvalidOperationException(validation.Code);
        }

        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private static EvidenceValidationResult Invalid(string code)
        => new(false, code);

    private static bool IsSafeIdentifier(string? value)
        => IsSafeToken(value, maxLength: 96);

    private static bool IsSafeBuildVersion(string? value)
    {
        if (string.Equals(value, Unbound, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '.'
            or '-'
            or '_'
            or '+');
    }

    private static bool IsBoundBuildVersion(string? value)
        => IsSafeBuildVersion(value) && !string.Equals(value, Unbound, StringComparison.Ordinal);

    private static bool HasValidHistory(ProtectedSendEvidenceRecord record)
    {
        if (record.TransitionHistory is null
            || record.TransitionHistory.Count != (int)record.State + 1
            || record.TransitionHistory[0] != DevelopmentEvidenceState.Proposed
            || record.TransitionHistory[^1] != record.State)
        {
            return false;
        }

        for (var index = 1; index < record.TransitionHistory.Count; index++)
        {
            if (!DevelopmentEvidenceStateTokens.TryAdvance(
                    record.TransitionHistory[index - 1],
                    record.TransitionHistory[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string? CompareExpectedArtifacts(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding expected)
    {
        if (expected.SchemaVersion != DevelopmentEvidenceContract.SchemaVersion)
        {
            return "unsupported_schema_version";
        }

        if (expected.ExecutableSha256 != Unbound
            && record.ExecutableSha256 != expected.ExecutableSha256)
        {
            return "evidence_executable_mismatch";
        }

        if (expected.InstallerIdentity != Unbound
            && record.InstallerIdentity != expected.InstallerIdentity)
        {
            return "evidence_installer_mismatch";
        }

        if (expected.CompatibilityFingerprint != Unbound
            && record.CompatibilityFingerprint != expected.CompatibilityFingerprint)
        {
            return "evidence_compatibility_mismatch";
        }

        if (expected.SubmitBinding != Unbound
            && record.SubmitBinding != expected.SubmitBinding)
        {
            return "evidence_binding_mismatch";
        }

        return null;
    }

    private static bool IsSafeCommit(string? value)
        => string.Equals(value, Unbound, StringComparison.Ordinal)
            || IsHex(value, 40, 64);

    private static bool IsSafeHash(string? value)
        => string.Equals(value, Unbound, StringComparison.Ordinal)
            || IsHex(value, 64, 64);

    private static bool IsHex(string? value, int minimumLength, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < minimumLength
            || value.Length > maximumLength)
        {
            return false;
        }

        return value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
    }

    private static bool IsSafeClaim(string? value)
        => value is "unverified" or FixedClaim or ReleasedClaim;

    private static bool IsSafeToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_');
    }
}

public static class DevelopmentEvidenceContractSmoke
{
    public static bool Run()
    {
        var record = new ProtectedSendEvidenceRecord(
            SchemaVersion: DevelopmentEvidenceContract.SchemaVersion,
            TicketId: "ticket_351",
            BehaviorId: "protected_send_evidence",
            State: DevelopmentEvidenceState.Implemented,
            ReproductionId: "repro_protected_send",
            ReproductionCommandId: "cmd_repro_protected_send",
            HighestRequiredSeam: "deterministic_transaction",
            BuildVersion: BuildVersion.Current,
            SourceCommit: "unbound",
            ExecutableSha256: "unbound",
            InstallerIdentity: "unbound",
            CompatibilityFingerprint: "unbound",
            SubmitBinding: "unbound",
            TransitionHistory: EvidenceHistory(DevelopmentEvidenceState.Implemented));
        var valid = ProtectedSendEvidenceValidator.Validate(
            record,
            BuildVersion.Current,
            "unbound",
            DevelopmentEvidenceState.Implemented);
        var prematureFixed = ProtectedSendEvidenceValidator.Validate(
            record with { Claim = "fixed" },
            BuildVersion.Current,
            "unbound",
            DevelopmentEvidenceState.Implemented);

        return valid.Valid
            && !prematureFixed.Valid
            && prematureFixed.Code == "fixed_claim_requires_live_verified"
            && ProtectedSendEvidenceValidator.Serialize(record).Contains(
                "\"state\":\"implemented\"",
                StringComparison.Ordinal);
    }

    private static DevelopmentEvidenceState[] EvidenceHistory(DevelopmentEvidenceState state)
    {
        var history = new DevelopmentEvidenceState[(int)state + 1];
        for (var index = 0; index < history.Length; index++)
        {
            history[index] = (DevelopmentEvidenceState)index;
        }

        return history;
    }
}
