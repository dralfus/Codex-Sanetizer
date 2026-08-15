using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

/// <summary>
/// Canonical synthetic ChatGPT discovery evidence for deterministic tests and smoke.
/// Overrides are explicit so a negative case cannot silently omit required evidence.
/// </summary>
internal sealed class ChatGptDiscoveryFixtureBuilder
{
    private readonly TextSurfaceDescriptor _surface;
    private readonly Dictionary<string, string> _diagnostics;
    private bool _allowIncomplete;

    internal ChatGptDiscoveryFixtureBuilder(TextSurfaceDescriptor? surface = null)
    {
        _surface = surface ?? TestSurfaceFactory.CreateNativeSubmitSurface("chatgpt-desktop");
        _diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application_identity_hash"] = ChatGptDiscoveryFixture.Fingerprint("application"),
            ["application_version_hash"] = ChatGptDiscoveryFixture.Fingerprint("version"),
            ["application_version_status"] = "available",
            ["package_full_name_hash"] = ChatGptDiscoveryFixture.Fingerprint("package"),
            ["executable_name_hash"] = ChatGptDiscoveryFixture.Fingerprint("executable"),
            ["process_name_hash"] = ChatGptDiscoveryFixture.Fingerprint("process"),
            ["window_identity_hash"] = ChatGptDiscoveryFixture.Fingerprint("window"),
            ["window_class_hash"] = ChatGptDiscoveryFixture.Fingerprint("window-class"),
            ["composer_class_hash"] = ChatGptDiscoveryFixture.Fingerprint("composer"),
            ["element_control_type"] = "ControlType.Group",
            ["element_framework_id"] = "Chrome",
            ["focused_element_hash"] = ChatGptDiscoveryFixture.Fingerprint("focused-element"),
            [SendControlEvidence.AutomationIdHashKey] = ChatGptDiscoveryFixture.Fingerprint("send-automation"),
            [SendControlEvidence.NameHashKey] = ChatGptDiscoveryFixture.Fingerprint("send-name")
        };
    }

    internal ChatGptDiscoveryFixtureBuilder WithApplicationIdentityFingerprint(string fingerprint)
        => WithFingerprint("application_identity_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithApplicationVersionFingerprint(string fingerprint)
        => WithFingerprint("application_version_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithPackageFullNameFingerprint(string fingerprint)
        => WithFingerprint("package_full_name_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithExecutableNameFingerprint(string fingerprint)
        => WithFingerprint("executable_name_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithProcessNameFingerprint(string fingerprint)
        => WithFingerprint("process_name_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithWindowIdentityFingerprint(string fingerprint)
        => WithFingerprint("window_identity_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithWindowClassFingerprint(string fingerprint)
        => WithFingerprint("window_class_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithComposerClassFingerprint(string fingerprint)
        => WithFingerprint("composer_class_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithFocusedElementFingerprint(string fingerprint)
        => WithFingerprint("focused_element_hash", fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithSendControlAutomationFingerprint(string fingerprint)
        => WithFingerprint(SendControlEvidence.AutomationIdHashKey, fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithSendControlNameFingerprint(string fingerprint)
        => WithFingerprint(SendControlEvidence.NameHashKey, fingerprint);

    internal ChatGptDiscoveryFixtureBuilder WithoutApplicationIdentityFingerprint()
    {
        _diagnostics.Remove("application_identity_hash");
        _allowIncomplete = true;
        return this;
    }

    internal TextSurfaceDiscoveryResult Build()
    {
        foreach (var key in ChatGptDesktopCompatibility.RequiredEvidenceKeys)
        {
            if (!_allowIncomplete && !_diagnostics.ContainsKey(key))
            {
                throw new InvalidOperationException($"The canonical ChatGPT fixture is missing required evidence: {key}.");
            }
        }

        return TextSurfaceDiscoveryResult.Success(
            _surface,
            new Dictionary<string, string>(_diagnostics, StringComparer.Ordinal));
    }

    private ChatGptDiscoveryFixtureBuilder WithFingerprint(string key, string fingerprint)
    {
        if (!OpaqueFingerprint.TryParse(fingerprint, out _))
        {
            throw new ArgumentException("Fixture overrides must be opaque fingerprints.", nameof(fingerprint));
        }

        _diagnostics[key] = fingerprint.ToLowerInvariant();
        return this;
    }
}

internal static class ChatGptDiscoveryFixture
{
    internal static TextSurfaceDiscoveryResult CreateVerified(TextSurfaceDescriptor? surface = null)
        => new ChatGptDiscoveryFixtureBuilder(surface).Build();

    internal static ChatGptDiscoveryFixtureBuilder CreateBuilder(TextSurfaceDescriptor? surface = null)
        => new(surface);

    internal static string Fingerprint(string source)
        => OpaqueFingerprint.FromSource(source).Value;
}
