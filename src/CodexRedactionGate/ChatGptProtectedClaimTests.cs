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
            new ChatGptLiveContractProof(
                "test-build",
                fingerprint,
                "Ctrl+Enter",
                "sent_safely",
                true,
                CreateSafeTrace(fingerprint)));

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
            new ChatGptLiveContractProof(
                proofBuild,
                proofFingerprint,
                "Ctrl+Enter",
                "sent_safely",
                true,
                CreateSafeTrace(proofFingerprint)));

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
                VerificationFingerprint = default
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
    public void MalformedProofStoreIsRejectedBeforeClaimEvaluation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-malformed-claim-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        layout.EnsureDirectories();

        try
        {
            File.WriteAllText(
                ChatGptAcceptanceProofStore.DefaultPath(layout),
                "{\"live_contract\":{\"trace\":[null]}}\n");

            var loaded = ChatGptAcceptanceProofStore.Load(layout);

            Assert.That(loaded.Succeeded, Is.False);
            Assert.That(loaded.Code, Is.EqualTo("proofs_invalid"));
            Assert.That(loaded.Proofs, Is.EqualTo(ChatGptAcceptanceProofBundle.Empty));
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
            new ChatGptLiveContractProof(
                "test-build",
                fingerprint,
                "Ctrl+Enter",
                "sent_safely",
                true,
                CreateSafeTrace(fingerprint)));

        try
        {
            Assert.That(ChatGptAcceptanceProofStore.Save(layout, bundle), Is.True);
            var loaded = ChatGptAcceptanceProofStore.Load(layout);

            Assert.That(loaded.Succeeded, Is.True);
            Assert.That(loaded.Proofs.Reference, Is.EqualTo(bundle.Reference));
            Assert.That(loaded.Proofs.LiveContract, Is.Not.Null);
            Assert.That(
                loaded.Proofs.LiveContract!.Trace.Select(entry => entry.Stage),
                Is.EqualTo(bundle.LiveContract!.Trace.Select(entry => entry.Stage)));
            var persisted = File.ReadAllText(ChatGptAcceptanceProofStore.DefaultPath(layout));
            Assert.That(persisted, Does.Not.Contain("secret"));
            Assert.That(persisted, Does.Not.Contain("192.168.10.25"));
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
    public void ResidentChatGptSendUsesResidentAdmissionWithoutReleaseProofs()
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
            activeSurfaceDiscovery: () => discovery);
        var evidence = new NativeSubmitResidentEvidence(
            ReadinessAdmitted: true,
            new ChatGptProtectedClaimResult(
                ChatGptProtectedClaimEvaluator.DegradedStatus,
                "unavailable",
                "unavailable",
                "resident_snapshot_unavailable"));

        try
        {
            var result = controller.HandleGesture(
                new NativeKeyGesture("Enter", Ctrl: true),
                residentEvidence: evidence);

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(result.SuppressOriginalInput, Is.True);
            Assert.That(result.Submitted, Is.False);
            Assert.That(result.Diagnostics["protected_claim_status"], Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
            Assert.That(result.Diagnostics["release_evidence_status"], Is.EqualTo("not_current"));
            Assert.That(result.Diagnostics, Does.Not.ContainKey("fail_closed_reason"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestCase(false, OsInteractionStatusIds.FailedClosed)]
    [TestCase(true, OsInteractionStatusIds.NativeSubmitGuarded)]
    public void NativeSubmitInterception_UsesCapturedResidentEvidenceForAdmission(
        bool readinessAdmitted,
        string expectedStatus)
    {
        var discovery = CreateDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            discovery);
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => discovery);
        var evidence = new NativeSubmitResidentEvidence(
            readinessAdmitted,
            new ChatGptProtectedClaimResult(
                ChatGptProtectedClaimEvaluator.DegradedStatus,
                "not_applicable",
                "not_applicable",
                "resident_snapshot"));

        var result = controller.HandleGesture(
            new NativeKeyGesture("Enter", Ctrl: true),
            residentEvidence: evidence);

        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void MissingResidentReadinessStillBlocksChatGptSendWithoutReleaseProofs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-claim-readiness-tests", Guid.NewGuid().ToString("N"));
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
            activeSurfaceDiscovery: () => discovery);
        var evidence = new NativeSubmitResidentEvidence(
            ReadinessAdmitted: false,
            NativeSubmitResidentEvidence.NotRequired.ChatGptClaim);

        try
        {
            var result = controller.HandleGesture(
                new NativeKeyGesture("Enter", Ctrl: true),
                residentEvidence: evidence);

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
            Assert.That(result.SuppressOriginalInput, Is.True);
            Assert.That(result.Diagnostics["fail_closed_reason"], Is.EqualTo("resident_readiness_unproven"));
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
    public void ResidentTrayKeepsChatGptSendActiveWhenReleaseProofsAreMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-claim-tray-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var discovery = CreateDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            discovery);
        var hook = new SanitizerTests.FakeNativeSubmitHookHost();
        var nativeController = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => discovery);

        try
        {
            var controller = TrayProtectionController.CreateTest(
                new SanitizerTests.FakeTrayHotkeyHost(),
                () => new OsInteractionResult(
                    OsInteractionStatusIds.Protected,
                    Surface: null,
                    SanitizationResult: null,
                    ConfirmationModel: null,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: new Dictionary<string, string>()),
                hook,
                nativeController,
                () => new OsInteractionResult(
                    OsInteractionStatusIds.Protected,
                    Surface: null,
                    SanitizationResult: null,
                    ConfirmationModel: null,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: new Dictionary<string, string>()),
                nativeProfile: profile,
                storageLayout: layout,
                activeSurfaceDiscovery: () => discovery);

            Assert.That(controller.Start(), Is.True);
            Assert.That(controller.State.NativeSubmitEnabled, Is.True);
            Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
            Assert.That(controller.State.ComposerProtected, Is.True);
            Assert.That(controller.State.ProtectedClaimStatus, Is.EqualTo(ChatGptProtectedClaimEvaluator.DegradedStatus));
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
            new ProtectedSendTraceEntry(1, 1, fingerprint, "target_matched", "target_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "composer_read", "capture_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "sanitized", "sanitization_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "send_injected", "submit_requested", 1),
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

    [Test]
    public void FailedLiveContractCaptureKeepsPersistentArmAndAllowsOneRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-live-arm-retry-tests", Guid.NewGuid().ToString("N"));
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
            profileSnapshot: NativeSubmitProfileSnapshot.FromProfile(profile) with
            {
                LiveContractCaptureArmed = true
            });

        try
        {
            Assert.That(
                ChatGptAcceptanceProofStore.RecordReference(
                    layout,
                    profile,
                    BuildVersion.Current,
                    passed: true,
                    terminalStatus: "passed"),
                Is.True);
            Assert.That(ChatGptAcceptanceProofStore.ArmLiveContract(layout, profile), Is.True);
            var evidence = new NativeSubmitResidentEvidence(
                ReadinessAdmitted: true,
                new ChatGptProtectedClaimResult(
                ChatGptProtectedClaimEvaluator.DegradedStatus,
                "passed",
                "armed",
                "resident_snapshot"));

            var first = controller.HandleGesture(
                new NativeKeyGesture("Enter", Ctrl: true),
                residentEvidence: evidence);

            Assert.That(first.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(first.Diagnostics["live_contract_capture"], Is.EqualTo("armed_reserved"));
            Assert.That(
                ChatGptAcceptanceProofStore.IsLiveContractArmed(layout, profile, BuildVersion.Current),
                Is.True);

            controller.ClearLiveContractCapture();
            var claimAfterFailure = ChatGptProtectedClaimEvaluator.Evaluate(profile, layout);

            Assert.That(claimAfterFailure.ReferenceStatus, Is.EqualTo("passed"));
            Assert.That(claimAfterFailure.LiveContractStatus, Is.EqualTo("armed"));

            var retry = controller.HandleGesture(
                new NativeKeyGesture("Enter", Ctrl: true),
                residentEvidence: evidence);

            Assert.That(retry.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(retry.Diagnostics["live_contract_capture"], Is.EqualTo("armed_reserved"));
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
        => SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            CreateDiscovery());

    private static TextSurfaceDiscoveryResult CreateDiscovery()
        => ChatGptDiscoveryFixture.CreateVerified();

    private static IReadOnlyList<ProtectedSendTraceEntry> CreateSafeTrace(string fingerprint)
    {
        return new[]
        {
            new ProtectedSendTraceEntry(1, 1, fingerprint, "send_detected", "checking_prompt", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "target_matched", "target_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "composer_read", "capture_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "sanitized", "sanitization_verified", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "send_injected", "submit_requested", 1),
            new ProtectedSendTraceEntry(1, 1, fingerprint, "sent_safely", "submitted", 1)
        };
    }
}
