using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class LocalProtectionRecoveryTests
{
    [Test]
    public void Inspect_UnreadableSecret_ReportsStableRecoveryRequiredStatus()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            File.WriteAllBytes(
                Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName),
                new DeterministicDataProtector().Protect(new byte[16]));

            var result = LocalProtectionRecovery.Inspect(layout, new DeterministicDataProtector());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(result.RecoveryRequired, Is.True);
            Assert.That(result.ToString(), Does.Not.Contain(tempDirectory));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Inspect_MalformedProtectedVault_ReportsStableRecoveryRequiredStatus()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            var protector = new DeterministicDataProtector();
            var secretPath = Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var vaultPath = Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
            _ = new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            File.WriteAllText(vaultPath, "{\"version\":1,\"storage_mode\":\"protected\",\"protected_payload\":\"not-base64\"}");

            var result = LocalProtectionRecovery.Inspect(layout, protector);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(result.ToString(), Does.Not.Contain(tempDirectory));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WithoutConfirmation_PreservesUnreadableArtifacts()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var secretPath = Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var originalSecret = new DeterministicDataProtector().Protect(new byte[16]);
            File.WriteAllBytes(secretPath, originalSecret);

            var result = LocalProtectionRecovery.Recover(layout, confirmed: false, new DeterministicDataProtector());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.ConfirmationRequiredCode));
            Assert.That(File.ReadAllBytes(secretPath), Is.EqualTo(originalSecret));
            Assert.That(Directory.Exists(Path.Combine(layout.RootDirectory, "recovery")), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WithConfirmation_QuarantinesOldArtifactsAndCreatesFreshProtectedState()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            var secretPath = Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var vaultPath = Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
            File.WriteAllBytes(secretPath, new DeterministicDataProtector().Protect(new byte[16]));
            File.WriteAllText(vaultPath, "not-a-vault");

            var protector = new DeterministicDataProtector();
            var result = LocalProtectionRecovery.Recover(layout, confirmed: true, protector);
            var recoveredSecret = new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            var recoveredVault = FileMappingVault.CreateProtected(vaultPath, recoveredSecret, protector);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveredCode));
            Assert.That(result.PreviousArtifactsPreserved, Is.True);
            Assert.That(result.VaultInitialized, Is.True);
            Assert.That(recoveredSecret, Has.Length.EqualTo(DpapiProtectedHmacSecretProvider.SecretSizeBytes));
            Assert.That(recoveredVault.TryGetOriginal("DOMAIN_REDACTED", out _), Is.False);
            Assert.That(
                Directory.GetFiles(Path.Combine(layout.RootDirectory, "recovery"), "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName),
                Does.Contain(DpapiProtectedHmacSecretProvider.DefaultSecretFileName));
            Assert.That(
                Directory.GetFiles(Path.Combine(layout.RootDirectory, "recovery"), "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName),
                Does.Contain(FileMappingVault.DefaultVaultFileName));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WhenFreshStateCreationFails_RestoresPreviousArtifacts()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            var secretPath = Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var vaultPath = Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
            var originalSecret = new DeterministicDataProtector().Protect(new byte[16]);
            const string originalVault = "not-a-vault";
            File.WriteAllBytes(secretPath, originalSecret);
            File.WriteAllText(vaultPath, originalVault);

            var result = LocalProtectionRecovery.Recover(layout, confirmed: true, new FailingProtectDataProtector());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryFailedCode));
            Assert.That(result.PreviousArtifactsPreserved, Is.True);
            Assert.That(File.ReadAllBytes(secretPath), Is.EqualTo(originalSecret));
            Assert.That(File.ReadAllText(vaultPath), Is.EqualTo(originalVault));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WhenFirstQuarantineMoveFails_PreservesOriginalArtifacts()
    {
        var state = CreateUnreadableState();
        try
        {
            var result = LocalProtectionRecovery.Recover(
                state.Layout,
                confirmed: true,
                new DeterministicDataProtector(),
                new RecoveryFileOperations(move: (source, destination) => throw new IOException("Test move failure.")));

            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryFailedCode));
            Assert.That(result.ToString(), Does.Not.Contain(state.Layout.RootDirectory));
            Assert.That(File.ReadAllBytes(state.SecretPath), Is.EqualTo(state.Secret));
            Assert.That(File.ReadAllText(state.VaultPath), Is.EqualTo(state.Vault));
        }
        finally
        {
            Directory.Delete(state.Layout.RootDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WhenSecondQuarantineMoveFails_PreservesOriginalArtifacts()
    {
        var state = CreateUnreadableState();
        try
        {
            var moveCount = 0;
            var result = LocalProtectionRecovery.Recover(
                state.Layout,
                confirmed: true,
                new DeterministicDataProtector(),
                new RecoveryFileOperations(move: (source, destination) =>
                {
                    moveCount++;
                    if (moveCount == 2)
                    {
                        throw new IOException("Test move failure.");
                    }

                    File.Move(source, destination);
                }));

            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryFailedCode));
            Assert.That(result.ToString(), Does.Not.Contain(state.Layout.RootDirectory));
            Assert.That(File.ReadAllBytes(state.SecretPath), Is.EqualTo(state.Secret));
            Assert.That(File.ReadAllText(state.VaultPath), Is.EqualTo(state.Vault));
        }
        finally
        {
            Directory.Delete(state.Layout.RootDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WhenRollbackCleanupFails_PreservesQuarantinedOriginal()
    {
        var state = CreateUnreadableState();
        try
        {
            var result = LocalProtectionRecovery.Recover(
                state.Layout,
                confirmed: true,
                new FailOnSecondProtectDataProtector(),
                new RecoveryFileOperations(delete: _ => throw new IOException("Test delete failure.")));

            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryFailedCode));
            Assert.That(result.ToString(), Does.Not.Contain(state.Layout.RootDirectory));
            Assert.That(ReadPreservedArtifact(state.SecretPath, state.Layout, DpapiProtectedHmacSecretProvider.DefaultSecretFileName), Is.EqualTo(state.Secret));
            Assert.That(ReadPreservedArtifactText(state.VaultPath, state.Layout, FileMappingVault.DefaultVaultFileName), Is.EqualTo(state.Vault));
        }
        finally
        {
            Directory.Delete(state.Layout.RootDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WhenRollbackRestoreFails_PreservesQuarantinedOriginal()
    {
        var state = CreateUnreadableState();
        try
        {
            var movesToQuarantine = 0;
            var recoveryDirectoryPrefix = Path.Combine(state.Layout.RootDirectory, "recovery") + Path.DirectorySeparatorChar;
            var result = LocalProtectionRecovery.Recover(
                state.Layout,
                confirmed: true,
                new FailingProtectDataProtector(),
                new RecoveryFileOperations(move: (source, destination) =>
                {
                    if (source.StartsWith(recoveryDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Test restore failure.");
                    }

                    movesToQuarantine++;
                    File.Move(source, destination);
                }));

            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveryFailedCode));
            Assert.That(result.ToString(), Does.Not.Contain(state.Layout.RootDirectory));
            Assert.That(movesToQuarantine, Is.EqualTo(2));
            Assert.That(ReadPreservedArtifact(state.SecretPath, state.Layout, DpapiProtectedHmacSecretProvider.DefaultSecretFileName), Is.EqualTo(state.Secret));
            Assert.That(ReadPreservedArtifactText(state.VaultPath, state.Layout, FileMappingVault.DefaultVaultFileName), Is.EqualTo(state.Vault));
        }
        finally
        {
            Directory.Delete(state.Layout.RootDirectory, recursive: true);
        }
    }

    [Test]
    public void Recover_WithProductionDpapi_MakesDoctorReportReadyWithoutRawPaths()
    {
        var state = CreateUnreadableState();
        try
        {
            var result = LocalProtectionRecovery.Recover(state.Layout, confirmed: true);
            var scannerManifest = ScannerPackageManifestResolver.CreateDefault(state.Layout.RootDirectory);
            var safeDisabledScanner = new DefaultScannerPackageResolution(
                scannerManifest,
                ScannerRuntimeConfigurationReport.SafeDisabledLocalPackageMissing,
                AnyScannerArtifactPresent: false);
            var report = ReadinessDoctor.Check(
                state.Layout,
                defaultScannerPackageProbe: () => safeDisabledScanner);
            var rendered = string.Join(Environment.NewLine, report.Items.Select(item => $"{item.Component}:{item.Status}:{item.Code}"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Code, Is.EqualTo(LocalProtectionRecovery.RecoveredCode));
            Assert.That(report.Items.Single(item => item.Component == "vault_secret"),
                Is.EqualTo(new ReadinessItem("vault_secret", "ready", "vault_secret_ready")));
            Assert.That(rendered, Does.Not.Contain(state.Layout.RootDirectory));
            Assert.That(rendered, Does.Not.Contain("not-a-vault"));
        }
        finally
        {
            Directory.Delete(state.Layout.RootDirectory, recursive: true);
        }
    }

    [Test]
    public void GetOrCreateSecret_ConcurrentCreationKeepsOneProtectedSecret()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretPath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            using var start = new Barrier(2);
            var protector = new DeterministicDataProtector();
            var first = Task.Run(() =>
            {
                start.SignalAndWait();
                return new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            });
            var second = Task.Run(() =>
            {
                start.SignalAndWait();
                return new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret();
            });

            Task.WaitAll(first, second);

            Assert.That(first.Result, Is.EqualTo(second.Result));
            Assert.That(
                new DpapiProtectedHmacSecretProvider(secretPath, protector).GetOrCreateSecret(),
                Is.EqualTo(first.Result));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void GetOrCreateSecretWithStatus_MarksOnlyTheCreatingCall()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretPath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var provider = new DpapiProtectedHmacSecretProvider(secretPath, new DeterministicDataProtector());

            var created = provider.GetOrCreateSecretWithStatus();
            var loaded = provider.GetOrCreateSecretWithStatus();

            Assert.That(created.CreatedProtectedSecret, Is.Not.Null);
            Assert.That(loaded.CreatedProtectedSecret, Is.Null);
            Assert.That(loaded.Secret, Is.EqualTo(created.Secret));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void RecoveryRequiredSanitizer_BlocksSubmissionWithoutRecordingPromptText()
    {
        const string prompt = "RECOVERY_PROMPT_SECRET";
        var result = new RecoveryRequiredSanitizer().Sanitize(new SanitizeRequest(
            new[] { new ContentPart("prompt", ContentSources.PromptText, prompt, new System.Collections.Generic.Dictionary<string, string>()) },
            new SanitizationContext("chatgpt-desktop", null, null, null, "default"),
            new SanitizationOptions(false, false, "os-adapter")));
        var rendered = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(rendered, Does.Not.Contain(prompt));
        Assert.That(rendered, Does.Contain(LocalProtectionRecovery.RecoveryRequiredCode));
    }

    [Test]
    public void RecoveryRequiredTrayTooltip_UsesOnlyTheRecoveryStatus()
    {
        var tooltip = TrayStatusFormatter.FormatRecoveryRequiredNotifyIconText(
            LocalProtectionRecovery.RecoveryRequiredCode);

        Assert.That(tooltip, Does.Contain(LocalProtectionRecovery.RecoveryRequiredCode));
        Assert.That(tooltip, Does.Not.Contain("protected"));
        Assert.That(tooltip.Length, Is.LessThanOrEqualTo(63));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static UnreadableLocalProtectionState CreateUnreadableState()
    {
        var tempDirectory = CreateTempDirectory();
        var layout = DefaultStorageLayout.Create(tempDirectory);
        layout.EnsureDirectories();
        var secretPath = Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
        var vaultPath = Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
        var secret = new DeterministicDataProtector().Protect(new byte[16]);
        const string vault = "not-a-vault";
        File.WriteAllBytes(secretPath, secret);
        File.WriteAllText(vaultPath, vault);
        return new UnreadableLocalProtectionState(layout, secretPath, vaultPath, secret, vault);
    }

    private static byte[] ReadPreservedArtifact(string sourcePath, DefaultStorageLayout layout, string fileName)
    {
        var recoveryFiles = Directory.Exists(Path.Combine(layout.RootDirectory, "recovery"))
            ? Directory.GetFiles(Path.Combine(layout.RootDirectory, "recovery"), fileName, SearchOption.AllDirectories)
            : Array.Empty<string>();
        var path = recoveryFiles.SingleOrDefault() ?? sourcePath;
        return File.ReadAllBytes(path);
    }

    private static string ReadPreservedArtifactText(string sourcePath, DefaultStorageLayout layout, string fileName)
    {
        var recoveryFiles = Directory.Exists(Path.Combine(layout.RootDirectory, "recovery"))
            ? Directory.GetFiles(Path.Combine(layout.RootDirectory, "recovery"), fileName, SearchOption.AllDirectories)
            : Array.Empty<string>();
        var path = recoveryFiles.SingleOrDefault() ?? sourcePath;
        return File.ReadAllText(path);
    }

    private sealed record UnreadableLocalProtectionState(
        DefaultStorageLayout Layout,
        string SecretPath,
        string VaultPath,
        byte[] Secret,
        string Vault);

    private sealed class FailingProtectDataProtector : IDataProtector
    {
        private readonly DeterministicDataProtector _inner = new();

        public string ProtectionKind => "test_failing_protect";

        public byte[] Protect(byte[] plaintext)
        {
            throw new InvalidOperationException("Test protection failure.");
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return _inner.Unprotect(protectedData);
        }
    }

    private sealed class FailOnSecondProtectDataProtector : IDataProtector
    {
        private readonly DeterministicDataProtector _inner = new();
        private int _protectCalls;

        public string ProtectionKind => "test_failing_second_protect";

        public byte[] Protect(byte[] plaintext)
        {
            _protectCalls++;
            if (_protectCalls == 2)
            {
                throw new InvalidOperationException("Test protection failure.");
            }

            return _inner.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return _inner.Unprotect(protectedData);
        }
    }
}
