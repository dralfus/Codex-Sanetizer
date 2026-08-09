using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ChatGptProtectedClaimTests
{
    [Test]
    public void MissingAcceptanceProofsKeepChatGptClaimDegraded()
    {
        var result = ChatGptProtectedClaimEvaluator.Evaluate(
            CreateProfile(),
            "test-build",
            ChatGptAcceptanceProofBundle.Empty);

        Assert.That(result.Status, Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
        Assert.That(result.ReferenceStatus, Is.EqualTo(ChatGptProtectedClaimEvaluator.MissingStatus));
        Assert.That(result.LiveContractStatus, Is.EqualTo(ChatGptProtectedClaimEvaluator.MissingStatus));
    }

    [Test]
    public void MatchingReferenceAndLiveProofsPublishProtectedClaim()
    {
        var profile = CreateProfile();
        var fingerprint = profile.CompatibilityEvidence!.VerificationId;
        var proofs = new ChatGptAcceptanceProofBundle(
            new ChatGptReferenceAcceptanceProof("test-build", fingerprint, true, "passed"),
            new ChatGptLiveContractProof("test-build", fingerprint, "Ctrl+Enter", "sent_safely", true));

        var result = ChatGptProtectedClaimEvaluator.Evaluate(profile, "test-build", proofs);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(result.ReferenceStatus, Is.EqualTo("passed"));
        Assert.That(result.LiveContractStatus, Is.EqualTo("passed"));
    }

    [TestCase("other-build", "test-build")]
    [TestCase("test-build", "other-fingerprint")]
    public void BuildOrFingerprintMismatchKeepsClaimDegraded(string proofBuild, string proofFingerprint)
    {
        var profile = CreateProfile();
        var proofs = new ChatGptAcceptanceProofBundle(
            new ChatGptReferenceAcceptanceProof(proofBuild, proofFingerprint, true, "passed"),
            new ChatGptLiveContractProof(proofBuild, proofFingerprint, "Ctrl+Enter", "sent_safely", true));

        var result = ChatGptProtectedClaimEvaluator.Evaluate(profile, "test-build", proofs);

        Assert.That(result.Status, Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
        Assert.That(result.Reason, Does.Contain("mismatch"));
    }

    [Test]
    public void InvalidFingerprintCannotPublishOrArmAChatGptClaim()
    {
        var profile = CreateProfile() with
        {
            CompatibilityEvidence = CreateProfile().CompatibilityEvidence! with
            {
                VerificationId = "raw-looking-fingerprint"
            }
        };

        var result = ChatGptProtectedClaimEvaluator.Evaluate(
            profile,
            "test-build",
            ChatGptAcceptanceProofBundle.Empty);
        var layout = DefaultStorageLayout.Create(
            Path.Combine(Path.GetTempPath(), "codex-redaction-gate-invalid-fingerprint-tests", Guid.NewGuid().ToString("N")));

        try
        {
            Assert.That(result.Protected, Is.False);
            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
            Assert.That(ChatGptAcceptanceProofStore.ArmLiveContract(layout, profile), Is.False);
        }
        finally
        {
            if (Directory.Exists(layout.RootDirectory))
            {
                Directory.Delete(layout.RootDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void LiveProofRequiresCompletedSafeTraceAndConfiguredBinding()
    {
        var profile = CreateProfile();
        var trace = new[]
        {
            new ProtectedSendTraceEntry(1, 1, new string('a', 64), "send_detected", "checking_prompt", 1),
            new ProtectedSendTraceEntry(1, 1, new string('a', 64), "terminal_blocked", "blocked", 1)
        };

        var created = ChatGptProtectedClaimEvaluator.TryCreateLiveProof(
            profile,
            "test-build",
            trace,
            out var proof);

        Assert.That(created, Is.False);
        Assert.That(proof, Is.Null);
    }

    [Test]
    public void ProofStoreRoundTripKeepsOnlyRawFreeAcceptanceRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-claim-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProfile();
        var fingerprint = profile.CompatibilityEvidence!.VerificationId;
        var bundle = new ChatGptAcceptanceProofBundle(
            new ChatGptReferenceAcceptanceProof("test-build", fingerprint, true, "passed"),
            new ChatGptLiveContractProof("test-build", fingerprint, "Ctrl+Enter", "sent_safely", true));

        try
        {
            Assert.That(ChatGptAcceptanceProofStore.Save(layout, bundle), Is.True);
            var loaded = ChatGptAcceptanceProofStore.Load(layout);

            Assert.That(loaded.Succeeded, Is.True);
            Assert.That(loaded.Proofs, Is.EqualTo(bundle));
            var persisted = File.ReadAllText(ChatGptAcceptanceProofStore.DefaultPath(layout));
            Assert.That(persisted, Does.Not.Contain("prompt"));
            Assert.That(persisted, Does.Not.Contain("secret"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentChatGptSendIsSuppressedUntilBothProofsMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-claim-controller-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var discovery = CreateDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            discovery);
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => discovery,
            setupLayout: layout);

        try
        {
            var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(result.Status, Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
            Assert.That(result.SuppressOriginalInput, Is.True);
            Assert.That(result.Submitted, Is.False);
            Assert.That(result.Diagnostics["protected_claim_status"], Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void LiveContractArmIsBoundToCurrentBuildAndConsumedAfterSafeTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-live-claim-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProfile();
        var fingerprint = profile.CompatibilityEvidence!.VerificationId;
        var trace = new[]
        {
            new ProtectedSendTraceEntry(1, 1, fingerprint, "send_detected", "checking_prompt", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "sent_safely", "submitted", 1)
        };

        try
        {
            Assert.That(ChatGptAcceptanceProofStore.ArmLiveContract(layout, profile), Is.True);
            Assert.That(
                ChatGptAcceptanceProofStore.IsLiveContractArmed(layout, profile, BuildVersion.Current),
                Is.True);
            Assert.That(
                ChatGptAcceptanceProofStore.RecordLiveContract(layout, profile, BuildVersion.Current, trace),
                Is.True);
            Assert.That(
                ChatGptAcceptanceProofStore.IsLiveContractArmed(layout, profile, BuildVersion.Current),
                Is.False);

            var loaded = ChatGptAcceptanceProofStore.Load(layout);
            Assert.That(loaded.Proofs.LiveContract, Is.Not.Null);
            Assert.That(loaded.Proofs.LiveContract!.TerminalStatus, Is.EqualTo("sent_safely"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SubmitBindingProfile CreateProfile()
    {
        var evidence = new SurfaceCompatibilityEvidence(
            "app", "package", "version", "exe", "process", "window",
            "Chrome", "Group", "composer", new string('b', 64), DateTimeOffset.UtcNow,
            "Ctrl+Enter", "Enter", "send-control");
        return new SubmitBindingProfile(
            "chatgpt-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: evidence,
            Diagnostics: new Dictionary<string, string>());
    }

    private static TextSurfaceDiscoveryResult CreateDiscovery()
    {
        return TextSurfaceDiscoveryResult.Success(
            new TextSurfaceDescriptor(
                "chatgpt-composer",
                "chatgpt-desktop",
                "ChatGPT Desktop",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                new SurfaceMetadata(ComposerStatus: OsInteractionStatusIds.SupportedComposer)),
            new Dictionary<string, string>
            {
                ["application_identity_hash"] = "application-hash",
                ["application_version_hash"] = "version-hash",
                ["application_version_status"] = "available",
                ["package_full_name_hash"] = "package-hash",
                ["executable_name_hash"] = "executable-hash",
                ["process_name_hash"] = "process-hash",
                ["window_identity_hash"] = "window-hash",
                ["window_class_hash"] = "window-class-hash",
                ["composer_class_hash"] = "composer-class-hash",
                ["element_control_type"] = "ControlType.Group",
                ["element_framework_id"] = "Chrome",
                ["focused_element_hash"] = "composer-hash"
            });
    }
}
