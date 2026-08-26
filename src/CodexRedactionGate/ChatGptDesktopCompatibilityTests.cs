using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ChatGptDesktopCompatibilityTests
{
    [Test]
    public void VerifyUserBindings_PinsChatGptFingerprintAndRejectsEachChangedField()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);

        Assert.That(profile.IsProtected, Is.True);
        Assert.That(profile.CompatibilityEvidence, Is.Not.Null);
        Assert.That(profile.CompatibilityEvidence!.DesktopIdentity?.IsComplete, Is.True);
        Assert.That(profile.CompatibilityEvidence.VerifiedTargetFingerprint?.IsComplete, Is.True);
        foreach (var key in profile.CompatibilityEvidence!.ToComparisonDiagnostics().Keys)
        {
            var changed = new Dictionary<string, string>(profile.CompatibilityEvidence.ToComparisonDiagnostics(), StringComparer.Ordinal)
            {
                [key] = "different"
            };
            var result = SurfaceCompatibilityEvaluator.Evaluate(profile, discovery.Surface, changed);
            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified), key);
        }
    }

    [Test]
    public void CompatibilityEvidence_UsesTheStoredOpaqueFingerprintWithoutRehashing()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var expected = OpaqueFingerprint.FromStored(discovery.Diagnostics["application_identity_hash"]);

        Assert.That(profile.CompatibilityEvidence, Is.Not.Null);
        Assert.That(profile.CompatibilityEvidence!.ApplicationIdentityFingerprint, Is.EqualTo(expected));
        Assert.That(
            profile.CompatibilityEvidence.ToComparisonDiagnostics()["application_identity_hash"],
            Is.EqualTo(expected.Value));
    }

    [Test]
    public void CanonicalDiscoveryFixture_IsCompleteAndMissingEvidenceFailsClosed()
    {
        var discovery = ChatGptDiscoveryFixture.CreateVerified();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var incomplete = ChatGptDiscoveryFixture.CreateBuilder()
            .WithoutApplicationIdentityFingerprint()
            .Build();
        var incompleteProfile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", incomplete);

        Assert.That(profile.IsProtected, Is.True);
        Assert.That(
            ChatGptDesktopCompatibility.RequiredEvidenceKeys.All(discovery.Diagnostics.ContainsKey),
            Is.True);
        Assert.That(incompleteProfile.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(incompleteProfile.IsProtected, Is.False);
    }

    [Test]
    public void CompatibilityIdentity_RejectsUnavailablePackageIdentity()
    {
        var discovery = ChatGptDiscoveryFixture.CreateBuilder()
            .WithoutPackageIdentity()
            .Build();

        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);

        Assert.That(profile.IsProtected, Is.False);
        Assert.That(profile.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
    }

    [Test]
    public void ActiveSendControlEvidence_IsRequiredToMatchPinnedFingerprint()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var changedDiscovery = ChatGptDiscoveryFixture.CreateBuilder()
            .WithSendControlAutomationFingerprint(ChatGptDiscoveryFixture.Fingerprint("changed-send-control"))
            .Build();

        var result = SurfaceCompatibilityEvaluator.Evaluate(
            profile,
            changedDiscovery.Surface!,
            ChatGptDesktopCompatibility.ActiveEvidence(profile, changedDiscovery));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.Diagnostics["mismatch_reason"], Does.Contain("send_control"));
    }

    [Test]
    public void CompatibilityEvidence_RemainsValidWhenOnlyTheRuntimeTargetChanges()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var nextRuntimeTarget = ChatGptDiscoveryFixture.CreateBuilder()
            .WithWindowIdentityFingerprint(ChatGptDiscoveryFixture.Fingerprint("next-window-instance"))
            .WithFocusedElementFingerprint(ChatGptDiscoveryFixture.Fingerprint("next-composer-instance"))
            .Build();

        var result = SurfaceCompatibilityEvaluator.Evaluate(
            profile,
            nextRuntimeTarget.Surface,
            ChatGptDesktopCompatibility.ActiveEvidence(profile, nextRuntimeTarget));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Protected));
    }

    [Test]
    public void CompatibilityEvidence_NormalizesCodexAndChatGptWindowBranding()
    {
        var codexDiscovery = ChatGptDiscoveryFixture.CreateBuilder()
            .WithWindowBranding("Codex")
            .Build();
        var chatGptDiscovery = ChatGptDiscoveryFixture.CreateBuilder()
            .WithWindowBranding("ChatGPT")
            .Build();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", codexDiscovery);

        var result = SurfaceCompatibilityEvaluator.Evaluate(
            profile,
            chatGptDiscovery.Surface,
            ChatGptDesktopCompatibility.ActiveEvidence(profile, chatGptDiscovery));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Protected));
    }

    [Test]
    public void VerifyUserBindings_DoesNotPersistTransientTargetDiagnostics()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", VerifiedChatGptDiscovery());

        var transientKeys = new[]
        {
            "surface.target_process_hash",
            "surface.window_identity_hash",
            "surface.focused_element_hash"
        };
        var serialized = System.Text.Json.JsonSerializer.Serialize(profile);

        foreach (var key in transientKeys)
        {
            Assert.That(profile.Diagnostics.ContainsKey(key), Is.False, key);
            Assert.That(serialized, Does.Not.Contain(key), key);
        }

        Assert.That(profile.CompatibilityEvidence!.VerifiedTargetFingerprint?.IsComplete, Is.True);
    }

    [Test]
    public void CompatibilityIdentity_RejectsUnsupportedPackageFamilyAndUnknownBranding()
    {
        var unsupportedPackage = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["package_family_name"] = "Microsoft.WindowsCalculator",
            ["window_branding"] = OpenAiDesktopIdentity.ProductId
        };
        var unknownBranding = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["package_family_name"] = OpenAiDesktopIdentity.SupportedPackageFamilyName,
            ["window_branding"] = "unknown"
        };

        Assert.That(OpenAiDesktopIdentity.TryCreate(unsupportedPackage, out _), Is.False);
        Assert.That(OpenAiDesktopIdentity.TryCreate(unknownBranding, out _), Is.False);
    }

    [Test]
    public void CompatibilityIdentity_AcceptsSupportedPackageFamilyWithDifferentCasing()
    {
        var diagnostics = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["package_family_name"] = OpenAiDesktopIdentity.SupportedPackageFamilyName.ToLowerInvariant()
        };

        Assert.That(OpenAiDesktopIdentity.TryCreate(diagnostics, out var identity), Is.True);
        Assert.That(identity!.PackageFamilyName, Is.EqualTo(OpenAiDesktopIdentity.SupportedPackageFamilyName));
    }

    [Test]
    public void CompatibilityIdentity_RejectsDifferentPackagePublisher()
    {
        var package = "OpenAI.Codex_1.0.0.0_x64__differentpublisher";

        Assert.That(OpenAiDesktopIdentity.IsSupportedPackageFullName(package), Is.False);
    }

    [TestCase("unknown")]
    [TestCase("unavailable")]
    [TestCase("not_available")]
    [TestCase("")]
    public void CompatibilityIdentity_RejectsEachPlaceholderFrameworkValue(string framework)
    {
        var diagnostics = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["element_framework_id"] = framework
        };

        Assert.That(OpenAiDesktopIdentity.TryCreate(diagnostics, out _), Is.False);
    }

    [TestCase("unknown")]
    [TestCase("unavailable")]
    [TestCase("not_available")]
    [TestCase("")]
    public void CompatibilityIdentity_RejectsEachPlaceholderControlValue(string controlType)
    {
        var diagnostics = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["element_control_type"] = controlType
        };

        Assert.That(OpenAiDesktopIdentity.TryCreate(diagnostics, out _), Is.False);
    }

    [Test]
    public void VerifyUserBindings_RevalidatesCurrentDiscoveryWhenOldEvidenceIsSupplied()
    {
        var verified = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", verified);
        var incomplete = ChatGptDiscoveryFixture.CreateBuilder()
            .WithoutPackageIdentity()
            .Build();

        var result = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            incomplete,
            profile.CompatibilityEvidence);

        Assert.That(result.IsProtected, Is.False);
        Assert.That(result.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
    }

    [Test]
    public void CompatibilityIdentity_RejectsPlaceholderFrameworkAndControlEvidence()
    {
        var diagnostics = new Dictionary<string, string>(
            VerifiedChatGptDiscovery().Diagnostics,
            StringComparer.Ordinal)
        {
            ["element_framework_id"] = "unknown",
            ["element_control_type"] = ""
        };

        Assert.That(OpenAiDesktopIdentity.TryCreate(diagnostics, out _), Is.False);
    }

    [Test]
    public void TransientTargetFingerprint_UsesTheOpaqueFingerprintContractForRuntimeIds()
    {
        var fingerprint = TransientTargetFingerprint.FingerprintRuntimeId(new[] { 42, 7, 9 });

        Assert.That(fingerprint.IsValid, Is.True);
        Assert.That(fingerprint.Value, Has.Length.EqualTo(64));
    }

    [Test]
    public void VerifyUserBindings_DoesNotTreatALegacyShortRuntimeHashAsCompatibilityEvidence()
    {
        var discovery = VerifiedChatGptDiscovery();
        var diagnostics = new Dictionary<string, string>(discovery.Diagnostics, StringComparer.Ordinal)
        {
            ["focused_element_hash"] = "2a2ebfde8f642871"
        };
        var nativeShapedDiscovery = discovery with { Diagnostics = diagnostics };

        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", nativeShapedDiscovery);

        Assert.That(profile.IsProtected, Is.True);
        Assert.That(profile.CompatibilityEvidence, Is.Not.Null);
        Assert.That(profile.CompatibilityEvidence!.VerifiedTargetFingerprint, Is.Null);
    }

    [Test]
    public void IdentifiedSendControl_UsesLiveEvidenceForProtectedSubmit()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));

        var accepted = controller.HandleIdentifiedSendControl(discovery);
        var changed = controller.HandleIdentifiedSendControl(
            ChatGptDiscoveryFixture.CreateBuilder()
                .WithSendControlNameFingerprint(ChatGptDiscoveryFixture.Fingerprint("changed-send-control"))
                .Build());

        Assert.That(accepted.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(changed.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(changed.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void VerifyUserBindings_LeavesChatGptUnsupportedWhenFingerprintEvidenceIsIncomplete()
    {
        var discovery = ChatGptDiscoveryFixture.CreateBuilder()
            .WithoutApplicationIdentityFingerprint()
            .Build();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);

        Assert.That(profile.IsProtected, Is.False);
        Assert.That(profile.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(profile.Diagnostics["compatibility"], Is.EqualTo("fingerprint_incomplete"));
    }

    [Test]
    public void RequirePinnedFingerprint_DowngradesLegacyProtectedChatGptProfile()
    {
        var legacy = new SubmitBindingProfile(
            "chatgpt-desktop", true, "user_verified",
            SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            SubmitKeyBinding.Parse("Enter").Binding!,
            OsInteractionStatusIds.Protected, null, new Dictionary<string, string>());

        var result = ChatGptDesktopCompatibility.RequirePinnedFingerprint(legacy);

        Assert.That(result.IsProtected, Is.False);
        Assert.That(result.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
    }

    [Test]
    public void FingerprintDiagnostics_AreRawFree()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", VerifiedChatGptDiscovery());
        profile = profile with
        {
            CompatibilityEvidence = profile.CompatibilityEvidence! with
            {
                ApplicationIdentityFingerprint = OpaqueFingerprint.FromSource("ChatGPT secret C:\\private\\prompt.txt"),
                ComposerClassFingerprint = OpaqueFingerprint.FromSource("Button Name: Send")
            }
        };
        var rendered = string.Join("\n", profile.ToRawFreeDiagnostics().Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.That(rendered, Does.Not.Contain("ChatGPT"));
        Assert.That(rendered, Does.Not.Contain("secret"));
        Assert.That(rendered, Does.Not.Contain("C:\\"));
        Assert.That(rendered, Does.Not.Contain("prompt.txt"));
        Assert.That(rendered, Does.Not.Contain("Button Name"));
    }

    [Test]
    public void IncompleteEvidence_DoesNotCreateAFingerprintDuringDiagnosticRendering()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", VerifiedChatGptDiscovery());
        var incomplete = profile.CompatibilityEvidence! with
        {
            SendControlEvidenceFingerprint = null
        };

        var diagnostics = incomplete.ToComparisonDiagnostics();

        Assert.That(incomplete.IsComplete, Is.False);
        Assert.That(diagnostics["send_control_evidence_hash"], Is.Empty);
    }

    [Test]
    public void PinnedFingerprint_PersistsAcrossProfileStoreRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-fingerprint-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", VerifiedChatGptDiscovery());

        try
        {
            var saved = SubmitBindingProfileStore.Save(layout, new[] { profile });
            var loaded = SubmitBindingProfileStore.Load(layout);

            Assert.That(saved.Succeeded, Is.True);
            Assert.That(loaded.Succeeded, Is.True);
            Assert.That(loaded.Profiles.Single().CompatibilityEvidence, Is.Not.Null);
            Assert.That(loaded.Profiles.Single().CompatibilityEvidence!.VerifiedTargetFingerprint, Is.Null);
            Assert.That(
                loaded.Profiles.Single().CompatibilityEvidence!.ToComparisonDiagnostics(),
                Is.EqualTo(profile.CompatibilityEvidence!.ToComparisonDiagnostics()));
            Assert.That(ChatGptDesktopCompatibility.RequirePinnedFingerprint(loaded.Profiles.Single()).IsProtected, Is.True);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static TextSurfaceDiscoveryResult VerifiedChatGptDiscovery()
        => ChatGptDiscoveryFixture.CreateVerified();
}
