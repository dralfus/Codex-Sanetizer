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
    internal const string SupportedPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g";

    public string PackageFamilyName { get; init; } = "unknown";

    public string PackageIdentityStatus { get; init; } = "unavailable";

    public string WindowBranding { get; init; } = "unknown";

    internal static string NormalizeProductId(string? processName)
    {
        return IsOpenAiProcess(processName)
            ? ProductId
            : processName?.Trim() ?? string.Empty;
    }

    internal static string NormalizeExecutableName(string? executableName)
    {
        return IsOpenAiProcess(executableName)
            ? ProductId
            : executableName?.Trim() ?? string.Empty;
    }

    internal static string NormalizeWindowBranding(string? windowTitle)
    {
        return !string.IsNullOrWhiteSpace(windowTitle)
            && (windowTitle.Contains("codex", StringComparison.OrdinalIgnoreCase)
                || windowTitle.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
            ? ProductId
            : "unknown";
    }

    internal static string NormalizePackageFamilyName(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return string.Empty;
        }

        var separator = packageFullName.IndexOf('_');
        if (separator <= 0)
        {
            return packageFullName;
        }

        var packageName = packageFullName[..separator];
        var publisherSeparator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (publisherSeparator >= 0 && publisherSeparator + 2 < packageFullName.Length)
        {
            return $"{packageName}_{packageFullName[(publisherSeparator + 2)..]}";
        }

        return packageFullName;
    }

    internal static bool IsSupportedPackageFullName(string? packageFullName)
    {
        return string.Equals(
            NormalizePackageFamilyName(packageFullName),
            SupportedPackageFamilyName,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMeaningfulEvidenceValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("not_available", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("missing", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSupportedProfileId(string profileId)
    {
        return profileId is "codex-desktop" or "chatgpt-desktop";
    }

    internal static IReadOnlyList<string> RequiredEvidenceKeys { get; } = new[]
    {
        "application_identity_hash",
        "application_version_hash",
        "application_version_status",
        "package_identity_status",
        "package_family_name",
        "package_full_name_hash",
        "executable_name_hash",
        "process_name_hash",
        "window_class_hash",
        "window_branding",
        "composer_class_hash",
        "element_control_type",
        "element_framework_id"
    };

    public bool IsComplete =>
        ApplicationIdentityFingerprint.IsValid
        && ApplicationVersionFingerprint.IsValid
        && string.Equals(ApplicationVersionStatus, "available", StringComparison.Ordinal)
        && PackageFullNameFingerprint.IsValid
        && string.Equals(PackageIdentityStatus, "available", StringComparison.Ordinal)
        && string.Equals(PackageFamilyName, SupportedPackageFamilyName, StringComparison.OrdinalIgnoreCase)
        && ExecutableNameFingerprint.IsValid
        && ProcessNameFingerprint.IsValid
        && WindowClassFingerprint.IsValid
        && string.Equals(WindowBranding, ProductId, StringComparison.Ordinal)
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
            ["package_identity_status"] = PackageIdentityStatus,
            ["package_family_name"] = PackageFamilyName,
            ["package_full_name_hash"] = PackageFullNameFingerprint.Value,
            ["executable_name_hash"] = ExecutableNameFingerprint.Value,
            ["process_name_hash"] = ProcessNameFingerprint.Value,
            ["window_class_hash"] = WindowClassFingerprint.Value,
            ["window_branding"] = WindowBranding,
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
            || !diagnostics.TryGetValue("package_identity_status", out var packageIdentityStatus)
            || !string.Equals(packageIdentityStatus, "available", StringComparison.Ordinal)
            || !TryReadCanonicalValue(diagnostics, "package_family_name", SupportedPackageFamilyName, out var packageFamilyName)
            || !TryReadCanonicalValue(diagnostics, "window_branding", ProductId, out var windowBranding)
            || !TryReadMeaningfulValue(diagnostics, "element_framework_id", out var frameworkId)
            || !TryReadMeaningfulValue(diagnostics, "element_control_type", out var controlType)
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
            OpaqueFingerprint.FromSource(frameworkId),
            OpaqueFingerprint.FromSource(controlType),
            composerClass);
        identity = identity with
        {
            PackageFamilyName = packageFamilyName,
            PackageIdentityStatus = packageIdentityStatus,
            WindowBranding = windowBranding
        };
        return identity.IsComplete;
    }

    private static bool TryReadCanonicalValue(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        string expected,
        out string value)
    {
        value = string.Empty;
        if (!diagnostics.TryGetValue(key, out var candidate)
            || !string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = expected;
        return true;
    }

    private static bool TryReadMeaningfulValue(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!diagnostics.TryGetValue(key, out var candidate)
            || !IsMeaningfulEvidenceValue(candidate))
        {
            return false;
        }

        var normalized = candidate.Trim();
        value = normalized;
        return true;
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

    private static bool IsOpenAiProcess(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = System.IO.Path.GetFileNameWithoutExtension(value.Trim());
        return string.Equals(normalized, "codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "chatgpt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "chat gpt", StringComparison.OrdinalIgnoreCase);
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
