using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

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
    string TicketId,
    string BehaviorId,
    DevelopmentEvidenceState State,
    string ReproductionId,
    string HighestRequiredSeam,
    string BuildVersion,
    string SourceCommit,
    string ExecutableSha256,
    string InstallerIdentity,
    string CompatibilityFingerprint,
    string SubmitBinding,
    string Claim = "unverified");

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
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!Enum.IsDefined(record.State))
        {
            return Invalid("invalid_evidence_state");
        }

        if (!Enum.IsDefined(minimumState))
        {
            return Invalid("invalid_required_evidence_state");
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

        if (!IsSafeIdentifier(record.HighestRequiredSeam))
        {
            return Invalid("invalid_highest_required_seam");
        }

        if (!IsSafeBuildVersion(record.BuildVersion))
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

        if (!string.Equals(record.BuildVersion, expectedBuildVersion, StringComparison.Ordinal))
        {
            return Invalid("evidence_build_mismatch");
        }

        if (!string.Equals(record.SourceCommit, expectedSourceCommit, StringComparison.Ordinal))
        {
            return Invalid("evidence_source_mismatch");
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
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static bool Run()
    {
        var record = new ProtectedSendEvidenceRecord(
            TicketId: "ticket_351",
            BehaviorId: "protected_send_evidence",
            State: DevelopmentEvidenceState.Implemented,
            ReproductionId: "repro_protected_send",
            HighestRequiredSeam: "deterministic_transaction",
            BuildVersion: BuildVersion.Current,
            SourceCommit: SourceCommit,
            ExecutableSha256: Hash,
            InstallerIdentity: "installer_candidate",
            CompatibilityFingerprint: Hash,
            SubmitBinding: "ctrl_enter");
        var valid = ProtectedSendEvidenceValidator.Validate(
            record,
            BuildVersion.Current,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);
        var prematureFixed = ProtectedSendEvidenceValidator.Validate(
            record with { Claim = "fixed" },
            BuildVersion.Current,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);

        return valid.Valid
            && !prematureFixed.Valid
            && prematureFixed.Code == "fixed_claim_requires_live_verified"
            && ProtectedSendEvidenceValidator.Serialize(record).Contains(
                "\"state\":\"implemented\"",
                StringComparison.Ordinal);
    }
}
