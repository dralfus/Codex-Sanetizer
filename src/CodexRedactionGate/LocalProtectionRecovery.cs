using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexRedactionGate;

public sealed record LocalProtectionRecoveryResult(
    bool Succeeded,
    string Code,
    bool RecoveryRequired,
    bool ConfirmationRequired,
    bool PreviousArtifactsPreserved,
    bool VaultInitialized);

public static class LocalProtectionRecovery
{
    public const string ReadyCode = "local_protection_ready";
    public const string RecoveryRequiredCode = "local_protection_recovery_required";
    public const string ConfirmationRequiredCode = "local_protection_recovery_confirmation_required";
    public const string RecoveredCode = "local_protection_recovered";
    public const string RecoveryFailedCode = "local_protection_recovery_failed";
    public const string RecoveryNotRequiredCode = "local_protection_recovery_not_required";

    public static LocalProtectionRecoveryResult Inspect(
        DefaultStorageLayout layout,
        IDataProtector? dataProtector = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            layout.EnsureDirectories();
            var protector = dataProtector ?? new WindowsDpapiDataProtector();
            var secretPath = SecretPath(layout);
            var vaultPath = VaultPath(layout);
            if (!File.Exists(secretPath) && File.Exists(vaultPath))
            {
                return RecoveryRequired();
            }

            var secret = new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            _ = FileMappingVault.CreateProtected(vaultPath, secret, protector);
            return new LocalProtectionRecoveryResult(
                Succeeded: true,
                Code: ReadyCode,
                RecoveryRequired: false,
                ConfirmationRequired: false,
                PreviousArtifactsPreserved: false,
                VaultInitialized: File.Exists(vaultPath));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return RecoveryRequired();
        }
    }

    public static LocalProtectionRecoveryResult Recover(
        DefaultStorageLayout layout,
        bool confirmed,
        IDataProtector? dataProtector = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var inspection = Inspect(layout, dataProtector);
        if (!inspection.RecoveryRequired)
        {
            return inspection with { Code = RecoveryNotRequiredCode };
        }

        if (!confirmed)
        {
            return new LocalProtectionRecoveryResult(
                Succeeded: false,
                Code: ConfirmationRequiredCode,
                RecoveryRequired: true,
                ConfirmationRequired: true,
                PreviousArtifactsPreserved: false,
                VaultInitialized: false);
        }

        var protector = dataProtector ?? new WindowsDpapiDataProtector();
        var secretPath = SecretPath(layout);
        var vaultPath = VaultPath(layout);
        var moved = new List<(string Source, string Backup)>();

        try
        {
            layout.EnsureDirectories();
            var recoveryDirectory = Path.Combine(
                layout.RootDirectory,
                "recovery",
                $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(recoveryDirectory);
            MoveToRecoveryIfPresent(secretPath, recoveryDirectory, moved);
            MoveToRecoveryIfPresent(vaultPath, recoveryDirectory, moved);

            var secret = new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            var vault = FileMappingVault.CreateProtected(vaultPath, secret, protector);
            vault.EnsureInitialized();

            return new LocalProtectionRecoveryResult(
                Succeeded: true,
                Code: RecoveredCode,
                RecoveryRequired: false,
                ConfirmationRequired: false,
                PreviousArtifactsPreserved: moved.Count > 0,
                VaultInitialized: File.Exists(vaultPath));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            DeleteFreshArtifacts(secretPath, vaultPath);
            RestoreMovedArtifacts(moved);
            return new LocalProtectionRecoveryResult(
                Succeeded: false,
                Code: RecoveryFailedCode,
                RecoveryRequired: true,
                ConfirmationRequired: false,
                PreviousArtifactsPreserved: moved.All(item => File.Exists(item.Source)),
                VaultInitialized: false);
        }
    }

    private static LocalProtectionRecoveryResult RecoveryRequired()
    {
        return new LocalProtectionRecoveryResult(
            Succeeded: false,
            Code: RecoveryRequiredCode,
            RecoveryRequired: true,
            ConfirmationRequired: false,
            PreviousArtifactsPreserved: false,
            VaultInitialized: false);
    }

    private static string SecretPath(DefaultStorageLayout layout)
    {
        return Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
    }

    private static string VaultPath(DefaultStorageLayout layout)
    {
        return Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
    }

    private static void MoveToRecoveryIfPresent(
        string source,
        string recoveryDirectory,
        ICollection<(string Source, string Backup)> moved)
    {
        if (!File.Exists(source))
        {
            return;
        }

        var backup = Path.Combine(recoveryDirectory, Path.GetFileName(source));
        File.Move(source, backup);
        moved.Add((source, backup));
    }

    private static void DeleteFreshArtifacts(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void RestoreMovedArtifacts(IEnumerable<(string Source, string Backup)> moved)
    {
        foreach (var item in moved.Reverse())
        {
            if (File.Exists(item.Backup) && !File.Exists(item.Source))
            {
                File.Move(item.Backup, item.Source);
            }
        }
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is DpapiSecretLoadFailureException
            or CryptographicException
            or JsonException
            or FormatException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException;
    }
}
