using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;

namespace CodexRedactionGate;

public sealed record ReadinessItem(
    string Component,
    string Status,
    string Code);

public sealed record ReadinessReport(
    bool Ready,
    IReadOnlyList<ReadinessItem> Items);

public static class ReadinessDoctor
{
    public static ReadinessReport Check(
        DefaultStorageLayout layout,
        MvpPackageManifest? manifest = null,
        Func<byte[]>? vaultSecretProbe = null,
        Func<DefaultScannerPackageResolution>? defaultScannerPackageProbe = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var items = new List<ReadinessItem>
        {
            CheckStorage(layout),
            CheckPolicy(layout),
            CheckVault(layout),
            CheckVaultSecret(vaultSecretProbe),
            CheckAudit(layout),
            CheckScanner(manifest, defaultScannerPackageProbe),
            CheckProjectFileProtection()
        };

        return new ReadinessReport(
            Ready: items.TrueForAll(item => item.Status is "ready" or "defaults_active" or "safe_disabled"),
            Items: items);
    }

    private static ReadinessItem CheckStorage(DefaultStorageLayout layout)
    {
        try
        {
            layout.EnsureDirectories();
            return new ReadinessItem("storage", "ready", "storage_ready");
        }
        catch (Exception exception) when (IsLocalIoFailure(exception))
        {
            return new ReadinessItem("storage", "failed", "storage_unavailable");
        }
    }

    private static ReadinessItem CheckPolicy(DefaultStorageLayout layout)
    {
        try
        {
            Directory.CreateDirectory(layout.PolicyDirectory);
            var activePolicyPath = PolicyActivationStore.ActivePolicyPath(layout.PolicyDirectory);
            var result = new TomlPolicyLoader().LoadOrDefault(activePolicyPath);
            return result.LoadedFromFile && result.Activated
                ? new ReadinessItem("policy", "ready", "policy_active")
                : new ReadinessItem("policy", "defaults_active", "policy_defaults_active");
        }
        catch (Exception exception) when (IsLocalIoFailure(exception))
        {
            return new ReadinessItem("policy", "failed", "policy_unavailable");
        }
    }

    private static ReadinessItem CheckVault(DefaultStorageLayout layout)
    {
        try
        {
            Directory.CreateDirectory(layout.VaultDirectory);
            return new ReadinessItem("vault", "ready", "vault_storage_ready");
        }
        catch (Exception exception) when (IsLocalIoFailure(exception))
        {
            return new ReadinessItem("vault", "failed", "vault_storage_unavailable");
        }
    }

    private static ReadinessItem CheckVaultSecret(Func<byte[]>? vaultSecretProbe)
    {
        try
        {
            var secret = vaultSecretProbe is null
                ? DpapiProtectedHmacSecretProvider.CreateProduction().GetOrCreateSecret()
                : vaultSecretProbe();

            return secret.Length > 0
                ? new ReadinessItem("vault_secret", "ready", "vault_secret_ready")
                : new ReadinessItem("vault_secret", "failed", "vault_secret_empty");
        }
        catch (DpapiSecretLoadFailureException ex)
        {
            // Capture crash without exposing sensitive data
            CaptureLocalCrash("dpapi_secret_load", ex);
            return new ReadinessItem("vault_secret", "failed", "vault_secret_dpapi_failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return new ReadinessItem("vault_secret", "failed", "vault_secret_unavailable");
        }
    }

    private static void CaptureLocalCrash(string component, Exception ex)
    {
        LocalCrashDiagnostics.CaptureDefault(ex, component, "readiness_dpapi_failure");
    }

    private static ReadinessItem CheckAudit(DefaultStorageLayout layout)
    {
        try
        {
            Directory.CreateDirectory(layout.AuditDirectory);
            var probePath = Path.Combine(layout.AuditDirectory, $".readiness-{Guid.NewGuid():N}.tmp");
            AtomicFileWriter.WriteAllBytes(probePath, Encoding.UTF8.GetBytes("ok"));
            File.Delete(probePath);
            return new ReadinessItem("audit", "ready", "audit_writable");
        }
        catch (Exception exception) when (IsLocalIoFailure(exception))
        {
            return new ReadinessItem("audit", "failed", "audit_unwritable");
        }
    }

    private static ReadinessItem CheckScanner(
        MvpPackageManifest? manifest,
        Func<DefaultScannerPackageResolution>? defaultScannerPackageProbe)
    {
        if (manifest is null)
        {
            var defaultPackage = defaultScannerPackageProbe is null
                ? ScannerPackageManifestResolver.ResolveDefault(AppContext.BaseDirectory)
                : defaultScannerPackageProbe();
            if (defaultPackage.Report.SafeDisabled)
            {
                return new ReadinessItem("scanner", "safe_disabled", defaultPackage.Report.WarningCode!);
            }

            return defaultPackage.Report.Valid
                ? new ReadinessItem("scanner", "ready", "scanner_ready")
                : new ReadinessItem("scanner", "failed", defaultPackage.Report.WarningCode ?? "scanner_configuration_invalid");
        }

        var scanner = ScannerRuntimeConfigurationValidator.Validate(manifest);
        return scanner.Valid
            ? new ReadinessItem("scanner", "ready", "scanner_ready")
            : new ReadinessItem("scanner", "failed", scanner.WarningCode ?? "scanner_configuration_invalid");
    }

    private static ReadinessItem CheckProjectFileProtection()
    {
        return new ReadinessItem("project_files", "safe_disabled", "project_file_broker_not_configured");
    }

    private static bool IsLocalIoFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or SecurityException;
    }
}
