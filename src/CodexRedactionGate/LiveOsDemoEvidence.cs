using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record LiveOsDemoSendGateResult(
    bool Enabled,
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics);

public static class LiveOsDemoEvidence
{
    private const string EvidenceFileName = "os-demo-apply-evidence.txt";
    private const string SendModeSettingsFileName = "live-send-settings.txt";

    public static string EvidencePath(DefaultStorageLayout? layout = null)
    {
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        return Path.Combine(resolvedLayout.RootDirectory, EvidenceFileName);
    }

    public static void MarkApplyOnlyPassed(string profileId, DefaultStorageLayout? layout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        Directory.CreateDirectory(resolvedLayout.RootDirectory);
        File.WriteAllLines(
            EvidencePath(resolvedLayout),
            new[]
            {
                "apply_only_passed=true",
                $"profile_id={profileId}",
                $"timestamp_utc={DateTimeOffset.UtcNow:O}"
            });
    }

    public static LiveOsDemoSendGateResult EnableSendMode(DefaultStorageLayout? layout = null)
    {
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        var evidence = ReadEvidence(resolvedLayout);
        if (!IsSupportedEvidence(evidence))
        {
            var diagnostics = CreateDiagnostics(resolvedLayout, evidence, settingEnabled: false);
            return new LiveOsDemoSendGateResult(false, OsInteractionStatusIds.EvidenceMissing, diagnostics);
        }

        Directory.CreateDirectory(resolvedLayout.SettingsDirectory);
        File.WriteAllLines(
            SendModeSettingsPath(resolvedLayout),
            new[]
            {
                "send_mode_enabled=true",
                $"timestamp_utc={DateTimeOffset.UtcNow:O}"
            });
        return Check(resolvedLayout);
    }

    public static LiveOsDemoSendGateResult DisableSendMode(DefaultStorageLayout? layout = null)
    {
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        Directory.CreateDirectory(resolvedLayout.SettingsDirectory);
        File.WriteAllLines(
            SendModeSettingsPath(resolvedLayout),
            new[]
            {
                "send_mode_enabled=false",
                $"timestamp_utc={DateTimeOffset.UtcNow:O}"
            });
        return Check(resolvedLayout);
    }

    public static LiveOsDemoSendGateResult Check(DefaultStorageLayout? layout = null)
    {
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        var evidence = ReadEvidence(resolvedLayout);
        var hasSupportedEvidence = IsSupportedEvidence(evidence);
        var settingEnabled = ReadSendModeSetting(resolvedLayout);

        var diagnostics = CreateDiagnostics(resolvedLayout, evidence, settingEnabled);

        if (!settingEnabled)
        {
            return new LiveOsDemoSendGateResult(false, OsInteractionStatusIds.SafetyDisabled, diagnostics);
        }

        if (!hasSupportedEvidence)
        {
            return new LiveOsDemoSendGateResult(false, OsInteractionStatusIds.EvidenceMissing, diagnostics);
        }

        return new LiveOsDemoSendGateResult(true, "send_gate_enabled", diagnostics);
    }

    private static string SendModeSettingsPath(DefaultStorageLayout layout)
    {
        return Path.Combine(layout.SettingsDirectory, SendModeSettingsFileName);
    }

    private static bool ReadSendModeSetting(DefaultStorageLayout layout)
    {
        var path = SendModeSettingsPath(layout);
        return File.Exists(path)
            && File.ReadLines(path).Any(line => string.Equals(line, "send_mode_enabled=true", StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, string> CreateDiagnostics(
        DefaultStorageLayout layout,
        ApplyEvidence evidence,
        bool settingEnabled)
    {
        var hasSupportedEvidence = IsSupportedEvidence(evidence);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["send_mode_setting_enabled"] = settingEnabled.ToString().ToLowerInvariant(),
            ["apply_evidence_present"] = evidence.ApplyOnlyPassed.ToString().ToLowerInvariant(),
            ["supported_apply_evidence_present"] = hasSupportedEvidence.ToString().ToLowerInvariant(),
            ["evidence_profile_id"] = string.IsNullOrWhiteSpace(evidence.ProfileId) ? "none" : evidence.ProfileId,
            ["evidence_path_length"] = EvidencePath(layout).Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["settings_path_length"] = SendModeSettingsPath(layout).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static bool IsSupportedEvidence(ApplyEvidence evidence)
    {
        return evidence.ApplyOnlyPassed
            && evidence.ProfileId is "codex-desktop" or "chatgpt-desktop";
    }

    private static ApplyEvidence ReadEvidence(DefaultStorageLayout layout)
    {
        var path = EvidencePath(layout);
        if (!File.Exists(path))
        {
            return new ApplyEvidence(false, null);
        }

        var lines = File.ReadLines(path).ToArray();
        var applyOnlyPassed = lines.Any(line => string.Equals(line, "apply_only_passed=true", StringComparison.Ordinal));
        var profileId = lines
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && string.Equals(parts[0], "profile_id", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .FirstOrDefault();

        return new ApplyEvidence(applyOnlyPassed, profileId);
    }

    private sealed record ApplyEvidence(bool ApplyOnlyPassed, string? ProfileId);
}
