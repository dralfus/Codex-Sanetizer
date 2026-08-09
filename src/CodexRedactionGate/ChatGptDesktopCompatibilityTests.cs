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
        var rendered = string.Join("\n", profile.ToRawFreeDiagnostics().Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.That(rendered, Does.Not.Contain("ChatGPT"));
        Assert.That(rendered, Does.Not.Contain("secret"));
        Assert.That(rendered, Does.Not.Contain("C:\\"));
    }

    [Test]
    public void PinnedFingerprint_SurvivesAtomicProfilePersistence()
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
            ["window_identity_hash"] = "window-hash",
            ["element_control_type"] = "ControlType.Group",
            ["element_framework_id"] = "Chrome",
            ["focused_element_hash"] = "composer-hash"
        });
    }
}
