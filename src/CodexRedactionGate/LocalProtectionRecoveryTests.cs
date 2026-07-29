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
}
