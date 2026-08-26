using System;
using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class DevelopmentEvidenceContractTests
{
    [Test]
    public void EvidenceStateTokens_AreStableAndRoundTrip()
    {
        Assert.That(DevelopmentEvidenceStateTokens.ToToken(DevelopmentEvidenceState.ReproducedRed), Is.EqualTo("reproduced_red"));
        Assert.That(
            DevelopmentEvidenceStateTokens.TryParse("live_verified", out var state),
            Is.True);
        Assert.That(state, Is.EqualTo(DevelopmentEvidenceState.LiveVerified));
        Assert.That(DevelopmentEvidenceStateTokens.TryParse("fixed", out _), Is.False);
    }

    [Test]
    public void EvidenceStateTransitions_AllowOnlyTheNextEvidenceLevel()
    {
        Assert.That(
            DevelopmentEvidenceStateTokens.TryAdvance(
                DevelopmentEvidenceState.Implemented,
                DevelopmentEvidenceState.LocallyVerified),
            Is.True);
        Assert.That(
            DevelopmentEvidenceStateTokens.TryAdvance(
                DevelopmentEvidenceState.Implemented,
                DevelopmentEvidenceState.LiveVerified),
            Is.False);
        Assert.That(
            DevelopmentEvidenceStateTokens.TryAdvance(
                DevelopmentEvidenceState.LiveVerified,
                DevelopmentEvidenceState.Implemented),
            Is.False);
    }

    [Test]
    public void Validator_AcceptsAnImplementedRecordWithMatchingBuildIdentity()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.Implemented),
            BuildVersion.Current,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.True);
        Assert.That(result.Code, Is.EqualTo("valid"));
    }

    [Test]
    public void Validator_RejectsFixedClaimBeforeLiveVerification()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.Implemented) with { Claim = "fixed" },
            BuildVersion.Current,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("fixed_claim_requires_live_verified"));
    }

    [Test]
    public void Validator_RejectsMismatchedBuildIdentity()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.LiveVerified),
            "0.1.other-build",
            SourceCommit,
            DevelopmentEvidenceState.LiveVerified);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("evidence_build_mismatch"));
    }

    [Test]
    public void Validator_AcceptsSdkInformationalVersionWithCommitSuffix()
    {
        var record = CreateRecord(DevelopmentEvidenceState.Implemented) with
        {
            BuildVersion = "1.0.0+0123456789abcdef"
        };

        var result = ProtectedSendEvidenceValidator.Validate(
            record,
            record.BuildVersion,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.True);
    }

    [Test]
    public void Validator_RejectsRawOrUnsafeEvidenceFields()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.Implemented) with
            {
                ReproductionId = "prompt=secret value"
            },
            BuildVersion.Current,
            SourceCommit,
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("invalid_reproduction_id"));
    }

    [Test]
    public void Validator_RejectsMissingSchemaAndReproductionCommand()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.Implemented) with
            {
                SchemaVersion = "",
                ReproductionCommandId = ""
            },
            CreateBinding(),
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("invalid_schema_version"));
    }

    [Test]
    public void Validator_RejectsSkippedEvidenceHistory()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(
                DevelopmentEvidenceState.Implemented,
                new[]
                {
                    DevelopmentEvidenceState.Proposed,
                    DevelopmentEvidenceState.Implemented
                }),
            CreateBinding(),
            DevelopmentEvidenceState.Implemented);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("invalid_evidence_history"));
    }

    [Test]
    public void Validator_RejectsUnboundArtifactsForLiveEvidence()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.LiveVerified),
            CreateBinding() with
            {
                ExecutableSha256 = "unbound",
                InstallerIdentity = "unbound",
                CompatibilityFingerprint = "unbound",
                SubmitBinding = "unbound"
            },
            DevelopmentEvidenceState.LiveVerified);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("expected_binding_incomplete"));
    }

    [Test]
    public void Validator_RejectsMismatchedArtifactIdentity()
    {
        var result = ProtectedSendEvidenceValidator.Validate(
            CreateRecord(DevelopmentEvidenceState.LiveVerified),
            CreateBinding() with { InstallerIdentity = "installer_other" },
            DevelopmentEvidenceState.LiveVerified);

        Assert.That(result.Valid, Is.False);
        Assert.That(result.Code, Is.EqualTo("evidence_installer_mismatch"));
    }

    [Test]
    public void Serialize_RejectsAnUnsafeRecordInsteadOfEmittingIt()
    {
        Assert.That(
            () => ProtectedSendEvidenceValidator.Serialize(
                CreateRecord(DevelopmentEvidenceState.Implemented) with
                {
                    BehaviorId = "raw secret prompt"
                }),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Serialize_RequiresExternalBindingForLiveEvidence()
    {
        Assert.That(
            () => ProtectedSendEvidenceValidator.Serialize(
                CreateRecord(DevelopmentEvidenceState.LiveVerified)),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("external_binding_required"));
    }

    [Test]
    public void EvidenceHistory_IsDefensivelyCopied()
    {
        var history = EvidenceHistory(DevelopmentEvidenceState.Implemented);
        var record = CreateRecord(DevelopmentEvidenceState.Implemented, history);

        history[2] = DevelopmentEvidenceState.LiveVerified;

        Assert.That(
            record.TransitionHistory[2],
            Is.EqualTo(DevelopmentEvidenceState.Implemented));
    }

    [Test]
    public void EvidenceSerialization_UsesTokensAndContainsNoPromptField()
    {
        var json = ProtectedSendEvidenceValidator.Serialize(
            CreateRecord(DevelopmentEvidenceState.LocallyVerified));

        Assert.That(json, Does.Contain("\"state\":\"locally_verified\""));
        Assert.That(json, Does.Not.Contain("raw_prompt"));
        Assert.That(json, Does.Not.Contain("original_value"));
        Assert.That(json, Does.Not.Contain("secret value"));
        Assert.That(JsonDocument.Parse(json).RootElement.GetProperty("claim").GetString(), Is.EqualTo("unverified"));
    }

    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ProtectedSendEvidenceBinding CreateBinding()
    {
        return new ProtectedSendEvidenceBinding(
            DevelopmentEvidenceContract.SchemaVersion,
            BuildVersion.Current,
            SourceCommit,
            Hash,
            "installer_candidate",
            Hash,
            "ctrl_enter");
    }

    private static ProtectedSendEvidenceRecord CreateRecord(
        DevelopmentEvidenceState state,
        IReadOnlyList<DevelopmentEvidenceState>? history = null)
    {
        return new ProtectedSendEvidenceRecord(
            SchemaVersion: DevelopmentEvidenceContract.SchemaVersion,
            TicketId: "ticket_351",
            BehaviorId: "protected_send_evidence",
            State: state,
            ReproductionId: "repro_protected_send",
            ReproductionCommandId: "cmd_repro_protected_send",
            HighestRequiredSeam: "deterministic_transaction",
            BuildVersion: BuildVersion.Current,
            SourceCommit: SourceCommit,
            ExecutableSha256: Hash,
            InstallerIdentity: "installer_candidate",
            CompatibilityFingerprint: Hash,
            SubmitBinding: "ctrl_enter",
            TransitionHistory: history ?? EvidenceHistory(state));
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
