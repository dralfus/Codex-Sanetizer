using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    [Test]
    public void CurrentEvidenceRecordLocation_IsStableAndRepositoryRelative()
    {
        Assert.That(
            DevelopmentEvidenceRecordLocation.RelativePath,
            Is.EqualTo(Path.Combine("artifacts", "evidence", "351.json")));

        var root = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-evidence-root");
        Directory.CreateDirectory(root);
        try
        {
            var resolved = DevelopmentEvidenceRecordLocation.Resolve(root);

            Assert.That(resolved, Is.EqualTo(Path.Combine(root, DevelopmentEvidenceRecordLocation.RelativePath)));
            Assert.That(
                DevelopmentEvidenceRecordLocation.ForTicket("42"),
                Is.EqualTo(Path.Combine("artifacts", "evidence", "42.json")));
            Assert.That(
                () => DevelopmentEvidenceRecordLocation.ForTicket("ticket_42"),
                Throws.TypeOf<ArgumentException>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsMissingRecord()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("current_evidence_record_missing"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsSyntheticRecordBelowRequiredState()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var synthetic = CreateRecord(DevelopmentEvidenceState.Implemented) with
            {
                ExecutableSha256 = "unbound",
                InstallerIdentity = "unbound",
                CompatibilityFingerprint = "unbound",
                SubmitBinding = "unbound",
                ValidatorArtifactSha256 = "unbound"
            };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(synthetic));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("current_evidence_state_below_required"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_AcceptsBoundLocallyVerifiedRecord()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            WriteCurrentRecord(
                root,
                ProtectedSendEvidenceValidator.Serialize(
                    CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
                    {
                        VerificationArtifactSha256 = binding.VerificationArtifactSha256
                    },
                    binding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.True);
            Assert.That(result.Code, Is.EqualTo("valid"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsStaleArtifactBinding()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var staleRecord = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                ExecutableSha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
                VerificationArtifactSha256 = binding.VerificationArtifactSha256
            };
            var staleBinding = binding with { ExecutableSha256 = staleRecord.ExecutableSha256 };
            WriteCurrentRecord(
                root,
                ProtectedSendEvidenceValidator.Serialize(staleRecord, staleBinding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("evidence_executable_mismatch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsMalformedRecord()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            WriteCurrentRecord(root, "{ not-json");

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("current_evidence_record_invalid"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsIncompleteVerificationArtifact()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var incomplete = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                VerificationArtifactSha256 = "unbound"
            };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(incomplete));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("current_evidence_binding_incomplete"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsStaleSourceCommit()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var stale = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                SourceCommit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
                VerificationArtifactSha256 = binding.VerificationArtifactSha256
            };
            var staleBinding = binding with { SourceCommit = stale.SourceCommit };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(stale, staleBinding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("evidence_source_mismatch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsStaleBuildVersion()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var stale = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                BuildVersion = "0.1.stale-build",
                VerificationArtifactSha256 = binding.VerificationArtifactSha256
            };
            var staleBinding = binding with { BuildVersion = stale.BuildVersion };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(stale, staleBinding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("evidence_build_mismatch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsStaleValidatorArtifact()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var staleHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
            var stale = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                ValidatorArtifactSha256 = staleHash,
                VerificationArtifactSha256 = binding.VerificationArtifactSha256
            };
            var staleBinding = binding with { ValidatorArtifactSha256 = staleHash };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(stale, staleBinding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("evidence_validator_artifact_mismatch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CurrentEvidenceGate_RejectsContractMismatch()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var binding = CreateCurrentBinding(root);
            var wrongContract = CreateRecord(DevelopmentEvidenceState.LocallyVerified) with
            {
                BehaviorId = "other_behavior",
                VerificationArtifactSha256 = binding.VerificationArtifactSha256
            };
            WriteCurrentRecord(root, ProtectedSendEvidenceValidator.Serialize(wrongContract, binding));

            var result = ProtectedSendEvidenceRecordGate.ValidateCurrent(
                root,
                binding,
                DevelopmentEvidenceState.LocallyVerified);

            Assert.That(result.Valid, Is.False);
            Assert.That(result.Code, Is.EqualTo("current_evidence_contract_mismatch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            "ctrl_enter",
            Hash,
            Hash);
    }

    private static ProtectedSendEvidenceBinding CreateCurrentBinding(string root)
    {
        const string proof = "evidence-proof";
        var path = DevelopmentEvidenceRecordLocation.ResolveVerificationArtifact(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, proof);
        var proofHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(proof)))
            .ToLowerInvariant();
        return CreateBinding() with { VerificationArtifactSha256 = proofHash };
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
            TransitionHistory: history ?? EvidenceHistory(state),
            ValidatorArtifactSha256: Hash,
            VerificationArtifactSha256: Hash);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteCurrentRecord(string root, string json)
    {
        var path = DevelopmentEvidenceRecordLocation.Resolve(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
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
