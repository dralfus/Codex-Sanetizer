using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public sealed record OpenAiDesktopIdentity(
    OpaqueFingerprint ApplicationIdentityFingerprint,
    OpaqueFingerprint ApplicationVersionFingerprint,
    string ApplicationVersionStatus,
    OpaqueFingerprint PackageFullNameFingerprint,
    OpaqueFingerprint ExecutableNameFingerprint,
    OpaqueFingerprint ProcessNameFingerprint,
    OpaqueFingerprint WindowClassFingerprint,
    OpaqueFingerprint FrameworkFingerprint,
    OpaqueFingerprint ControlTypeFingerprint,
    OpaqueFingerprint ComposerClassFingerprint)
{
    public const string ProductId = "openai-desktop";

    internal static string NormalizeProductId(string processName)
    {
        return IsOpenAiProcess(processName)
            ? ProductId
            : processName;
    }

    internal static IReadOnlyList<string> RequiredEvidenceKeys { get; } = new[]
    {
        "application_identity_hash",
        "application_version_hash",
        "application_version_status",
        "package_full_name_hash",
        "executable_name_hash",
        "process_name_hash",
        "window_class_hash",
        "composer_class_hash",
        "element_control_type",
        "element_framework_id"
    };

    public bool IsComplete =>
        ApplicationIdentityFingerprint.IsValid
        && ApplicationVersionFingerprint.IsValid
        && string.Equals(ApplicationVersionStatus, "available", StringComparison.Ordinal)
        && PackageFullNameFingerprint.IsValid
        && ExecutableNameFingerprint.IsValid
        && ProcessNameFingerprint.IsValid
        && WindowClassFingerprint.IsValid
        && FrameworkFingerprint.IsValid
        && ControlTypeFingerprint.IsValid
        && ComposerClassFingerprint.IsValid;

    public IReadOnlyDictionary<string, string> ToComparisonDiagnostics()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application_identity_hash"] = ApplicationIdentityFingerprint.Value,
            ["application_version_hash"] = ApplicationVersionFingerprint.Value,
            ["application_version_status"] = ApplicationVersionStatus,
            ["package_full_name_hash"] = PackageFullNameFingerprint.Value,
            ["executable_name_hash"] = ExecutableNameFingerprint.Value,
            ["process_name_hash"] = ProcessNameFingerprint.Value,
            ["window_class_hash"] = WindowClassFingerprint.Value,
            ["element_framework_id"] = FrameworkFingerprint.Value,
            ["element_control_type"] = ControlTypeFingerprint.Value,
            ["composer_class_hash"] = ComposerClassFingerprint.Value
        };
    }

    internal static bool TryCreate(
        IReadOnlyDictionary<string, string> diagnostics,
        out OpenAiDesktopIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        identity = null;
        if (RequiredEvidenceKeys.Any(key =>
                !diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            || !diagnostics.TryGetValue("application_version_status", out var versionStatus)
            || !string.Equals(versionStatus, "available", StringComparison.Ordinal)
            || !TryReadFingerprint(diagnostics, "application_identity_hash", out var applicationIdentity)
            || !TryReadFingerprint(diagnostics, "application_version_hash", out var applicationVersion)
            || !TryReadFingerprint(diagnostics, "package_full_name_hash", out var packageFullName)
            || !TryReadFingerprint(diagnostics, "executable_name_hash", out var executableName)
            || !TryReadFingerprint(diagnostics, "process_name_hash", out var processName)
            || !TryReadFingerprint(diagnostics, "window_class_hash", out var windowClass)
            || !TryReadFingerprint(diagnostics, "composer_class_hash", out var composerClass))
        {
            return false;
        }

        identity = new OpenAiDesktopIdentity(
            applicationIdentity,
            applicationVersion,
            versionStatus,
            packageFullName,
            executableName,
            processName,
            windowClass,
            OpaqueFingerprint.FromSource(diagnostics["element_framework_id"]),
            OpaqueFingerprint.FromSource(diagnostics["element_control_type"]),
            composerClass);
        return identity.IsComplete;
    }

    private static bool TryReadFingerprint(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        out OpaqueFingerprint fingerprint)
    {
        if (diagnostics.TryGetValue(key, out var value)
            && OpaqueFingerprint.TryParse(value, out fingerprint))
        {
            return true;
        }

        fingerprint = default;
        return false;
    }

    private static bool IsOpenAiProcess(string value)
    {
        return string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "chatgpt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "chat gpt", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TransientTargetFingerprint(
    OpaqueFingerprint ProcessInstanceFingerprint,
    OpaqueFingerprint WindowFingerprint,
    OpaqueFingerprint FocusedElementFingerprint)
{
    public bool IsComplete => ProcessInstanceFingerprint.IsValid
        && WindowFingerprint.IsValid
        && FocusedElementFingerprint.IsValid;

    internal static TransientTargetFingerprint? TryCreate(IReadOnlyDictionary<string, string> diagnostics)
    {
        return diagnostics.TryGetValue("target_process_hash", out var process)
            && diagnostics.TryGetValue("window_identity_hash", out var window)
            && diagnostics.TryGetValue("focused_element_hash", out var element)
            && OpaqueFingerprint.TryParse(process, out var processFingerprint)
            && OpaqueFingerprint.TryParse(window, out var windowFingerprint)
            && OpaqueFingerprint.TryParse(element, out var elementFingerprint)
            ? new TransientTargetFingerprint(processFingerprint, windowFingerprint, elementFingerprint)
            : null;
    }

    internal static OpaqueFingerprint FingerprintRuntimeId(IEnumerable<int> runtimeId)
    {
        ArgumentNullException.ThrowIfNull(runtimeId);
        var source = string.Join(
            ".",
            runtimeId.Select(item => item.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return OpaqueFingerprint.FromSource(source);
    }
}
