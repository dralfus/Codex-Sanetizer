using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
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
        return Recover(layout, confirmed, dataProtector, RecoveryFileOperations.Physical);
    }

    internal static LocalProtectionRecoveryResult Recover(
        DefaultStorageLayout layout,
        bool confirmed,
        IDataProtector? dataProtector,
        RecoveryFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fileOperations);

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
        var created = new List<CreatedArtifact>();

        try
        {
            layout.EnsureDirectories();
            var recoveryDirectory = Path.Combine(
                layout.RootDirectory,
                "recovery",
                $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(recoveryDirectory);
            MoveToRecoveryIfPresent(secretPath, recoveryDirectory, moved, fileOperations);
            MoveToRecoveryIfPresent(vaultPath, recoveryDirectory, moved, fileOperations);

            var secretProvisioning = new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecretWithStatus();
            if (secretProvisioning.CreatedProtectedSecret is not null)
            {
                created.Add(new CreatedArtifact(secretPath, secretProvisioning.CreatedProtectedSecret));
            }

            var vault = FileMappingVault.CreateProtected(vaultPath, secretProvisioning.Secret, protector);
            var createdVault = vault.EnsureInitializedWithSnapshot();
            if (createdVault is not null)
            {
                created.Add(new CreatedArtifact(vaultPath, createdVault));
            }

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
            DeleteCreatedArtifacts(created, fileOperations);
            RestoreMovedArtifacts(moved, fileOperations);
            return new LocalProtectionRecoveryResult(
                Succeeded: false,
                Code: RecoveryFailedCode,
                RecoveryRequired: true,
                ConfirmationRequired: false,
                PreviousArtifactsPreserved: moved.All(item =>
                    fileOperations.FileExists(item.Source) || fileOperations.FileExists(item.Backup)),
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
        ICollection<(string Source, string Backup)> moved,
        RecoveryFileOperations fileOperations)
    {
        if (!fileOperations.FileExists(source))
        {
            return;
        }

        var backup = Path.Combine(recoveryDirectory, Path.GetFileName(source));
        fileOperations.Move(source, backup);
        moved.Add((source, backup));
    }

    private static void DeleteCreatedArtifacts(
        IEnumerable<CreatedArtifact> artifacts,
        RecoveryFileOperations fileOperations)
    {
        foreach (var artifact in artifacts)
        {
            try
            {
                if (fileOperations.FileExists(artifact.Path)
                    && CryptographicOperations.FixedTimeEquals(File.ReadAllBytes(artifact.Path), artifact.Contents))
                {
                    fileOperations.Delete(artifact.Path);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // The original artifact remains at its source or in quarantine.
            }
        }
    }

    private static void RestoreMovedArtifacts(
        IEnumerable<(string Source, string Backup)> moved,
        RecoveryFileOperations fileOperations)
    {
        foreach (var item in moved.Reverse())
        {
            try
            {
                if (fileOperations.FileExists(item.Backup) && !fileOperations.FileExists(item.Source))
                {
                    fileOperations.Move(item.Backup, item.Source);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // The original artifact remains in the quarantine directory.
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
            or SecurityException
            or InvalidOperationException;
    }

    private sealed record CreatedArtifact(string Path, byte[] Contents);
}

internal sealed class RecoveryFileOperations
{
    public static RecoveryFileOperations Physical { get; } = new();

    private readonly Action<string, string> _move;
    private readonly Action<string> _delete;

    public RecoveryFileOperations(
        Action<string, string>? move = null,
        Action<string>? delete = null)
    {
        _move = move ?? File.Move;
        _delete = delete ?? File.Delete;
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public void Move(string source, string destination)
    {
        _move(source, destination);
    }

    public void Delete(string path)
    {
        _delete(path);
    }
}
