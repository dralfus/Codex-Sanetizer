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
    public void ActiveSendControlEvidence_IsRequiredToMatchPinnedFingerprint()
    {
        var discovery = VerifiedChatGptDiscovery();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop", "Ctrl+Enter", "Enter", discovery);
        var changedDiagnostics = new Dictionary<string, string>(discovery.Diagnostics, StringComparer.Ordinal)
        {
            [SendControlEvidence.AutomationIdHashKey] = "changed-send-control"
        };
        var changedDiscovery = discovery with { Diagnostics = changedDiagnostics };

        var result = SurfaceCompatibilityEvaluator.Evaluate(
            profile,
            changedDiscovery.Surface!,
            ChatGptDesktopCompatibility.ActiveEvidence(profile, changedDiscovery));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.Diagnostics["mismatch_reason"], Does.Contain("send_control"));
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
        var changedDiagnostics = new Dictionary<string, string>(discovery.Diagnostics, StringComparer.Ordinal)
        {
            [SendControlEvidence.NameHashKey] = "changed-send-control"
        };
        var changed = controller.HandleIdentifiedSendControl(
            discovery with { Diagnostics = changedDiagnostics });

        Assert.That(accepted.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(changed.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(changed.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void VerifyUserBindings_LeavesChatGptUnsupportedWhenFingerprintEvidenceIsIncomplete()
    {
        var discovery = VerifiedChatGptDiscovery() with
        {
            Diagnostics = new Dictionary<string, string> { ["application_identity_hash"] = "a" }
        };
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
                PackageFamilyName = "ChatGPT secret C:\\private\\prompt.txt",
                ComposerClassName = "Button Name: Send"
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
    {
        var surface = new TextSurfaceDescriptor(
            "test-chatgpt", "chatgpt-desktop", "ChatGPT", true, true, true, true,
            new SurfaceMetadata(ComposerStatus: OsInteractionStatusIds.SupportedComposer));
        return TextSurfaceDiscoveryResult.Success(surface, new Dictionary<string, string>
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
            ["focused_element_hash"] = "composer-hash",
            [SendControlEvidence.AutomationIdHashKey] = "send-automation-hash",
            [SendControlEvidence.NameHashKey] = "send-name-hash"
        });
    }
}
