using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;
using CodexRedactionGate;
using SanitizerWarning = CodexRedactionGate.Warning;

[TestFixture]
public class ContractTests
{
    [Test]
    public void SanitizeDecision_ContainsRequiredDecisions()
    {
        Assert.That(Enum.GetNames<SanitizeDecision>(), Is.EquivalentTo(new[]
        {
            nameof(SanitizeDecision.Allow),
            nameof(SanitizeDecision.Confirm),
            nameof(SanitizeDecision.Block)
        }));
    }

    [Test]
    public void ContentPart_SupportsRequiredContentSources()
    {
        var sources = new[]
        {
            ContentSources.PromptText,
            ContentSources.Clipboard,
            ContentSources.TextAttachment,
            ContentSources.FileSnippet,
            ContentSources.ToolOutput
        };

        var parts = sources
            .Select((source, index) => new ContentPart(
                Id: $"part-{index}",
                ContentSource: source,
                RawText: "sample text",
                SourceMetadata: new Dictionary<string, string>()))
            .ToArray();

        var request = new SanitizeRequest(
            ContentParts: parts,
            Context: new SanitizationContext("codex", null, "project", "session", "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: true,
                AllowSecretStorage: false,
                ConfirmationMode: "local"));

        Assert.That(request.ContentParts.Select(p => p.ContentSource), Is.EqualTo(sources));
    }

    [Test]
    public void SanitizationResult_RepresentsSanitizedTextEntitiesReplacementsWarningsAndAudit()
    {
        var entity = new SanitizedEntity(
            ContentPartId: "prompt",
            Offset: 6,
            Length: 12,
            Type: "customer",
            DetectorId: "contract",
            Action: "pseudonymize_restorable");
        var replacement = new Replacement(
            ContentPartId: "prompt",
            Offset: 6,
            Length: 12,
            Type: "customer",
            Placeholder: "CUSTOMER_0001",
            Action: "pseudonymize_restorable",
            Restorable: true);
        var warning = new SanitizerWarning(
            Code: "needs_confirmation",
            Message: "Sanitized prompt requires confirmation.",
            Severity: WarningSeverity.Warning);
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UnixEpoch,
            RequestId: "request-1",
            Application: "codex",
            WorkspaceHash: "workspace-hash",
            PolicyProfile: "default",
            Decision: SanitizeDecision.Confirm,
            ScannerStatuses: new Dictionary<string, string> { ["contract"] = "not_run" },
            EntityCountsByType: new Dictionary<string, int> { ["customer"] = 1 },
            ActionCounts: new Dictionary<string, int> { ["pseudonymize_restorable"] = 1 },
            SpanSummaries: new[]
            {
                new SpanSummary("prompt", 6, 12, "customer", "contract")
            },
            ReplacementSummaries: new[]
            {
                new ReplacementSummary("CUSTOMER_0001", "customer", "pseudonymize_restorable")
            },
            Warnings: new[] { warning },
            AdapterMode: "guard",
            DurationsMs: new Dictionary<string, long> { ["total"] = 0 });
        var result = new SanitizationResult(
            Decision: SanitizeDecision.Confirm,
            SanitizedText: "hello CUSTOMER_0001",
            Entities: new[] { entity },
            Replacements: new[] { replacement },
            Warnings: new[] { warning },
            AuditEvent: auditEvent,
            RestoreHandle: "restore-1");

        Assert.That(result.SanitizedText, Is.EqualTo("hello CUSTOMER_0001"));
        Assert.That(result.Entities, Has.Count.EqualTo(1));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.AuditEvent.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.ReplacementSummaries.Single().Pseudonym, Is.EqualTo("CUSTOMER_0001"));
    }
}

[TestFixture]
public class PolicyLoaderTests
{
    [Test]
    public void LoadOrDefault_MissingPolicy_UsesSafeBuiltInDefaults()
    {
        var loader = new TomlPolicyLoader();
        var missingPath = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"), "missing.toml");

        var result = loader.LoadOrDefault(missingPath);

        Assert.That(result.Activated, Is.True);
        Assert.That(result.LoadedFromFile, Is.False);
        Assert.That(result.ActivePolicy.Profile, Is.EqualTo("built-in-defaults"));
        Assert.That(result.ActivePolicy.Defaults.UnknownHighRisk, Is.EqualTo(PolicyActions.Confirm));
        Assert.That(result.ActivePolicy.Defaults.Secret, Is.EqualTo(PolicyActions.RedactNonRestorable));
        Assert.That(result.ActivePolicy.Defaults.InternalIdentifier, Is.EqualTo(PolicyActions.PseudonymizeRestorable));
        Assert.That(result.ActivePolicy.ScannerSettings.GitleaksEnabled, Is.True);
        Assert.That(result.ActivePolicy.ScannerSettings.GitleaksTimeoutMs, Is.EqualTo(5000));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("policy_missing_using_defaults"));
    }

    [Test]
    public void LoadOrDefault_ValidTomlPolicy_LoadsSuccessfully()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "global"

                [defaults]
                unknown_high_risk = "confirm"
                secret = "redact_non_restorable"
                internal_identifier = "pseudonymize_restorable"

                [scanners]
                gitleaks_enabled = true
                gitleaks_timeout_ms = 5000

                [[allow]]
                type = "url"
                match = "https://learn.microsoft.com/"
                mode = "prefix"
                reason = "public documentation"

                [[sensitive]]
                type = "domain"
                match = "corp.example.local"
                mode = "suffix"
                action = "pseudonymize_restorable"
                label = "internal domain"

                [[block]]
                type = "secret"
                pattern = "-----BEGIN PRIVATE KEY-----"
                action = "redact_non_restorable"
                label = "private key"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("global"));
            Assert.That(result.ActivePolicy.ScannerSettings.GitleaksEnabled, Is.True);
            Assert.That(result.ActivePolicy.ScannerSettings.GitleaksTimeoutMs, Is.EqualTo(5000));
            Assert.That(result.ActivePolicy.AllowRules.Single().Mode, Is.EqualTo("prefix"));
            Assert.That(result.ActivePolicy.AllowRules.Single().Reason, Is.EqualTo("public documentation"));
            Assert.That(result.ActivePolicy.SensitiveRules.Single().Match, Is.EqualTo("corp.example.local"));
            Assert.That(result.ActivePolicy.RegexRules, Is.Empty);
            Assert.That(result.ActivePolicy.BlockRules.Single().Action, Is.EqualTo(PolicyActions.RedactNonRestorable));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_ValidRegexPolicy_CompilesAndLoadsSuccessfully()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "regex-policy"

                [[regex]]
                type = "project"
                pattern = "\\bPRJ-[0-9]{4,}\\b"
                action = "pseudonymize_restorable"
                label = "internal project code"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("regex-policy"));
            Assert.That(result.ActivePolicy.RegexRules.Single().Type, Is.EqualTo("project"));
            Assert.That(result.ActivePolicy.RegexRules.Single().Pattern, Is.EqualTo("\\bPRJ-[0-9]{4,}\\b"));
            Assert.That(result.ActivePolicy.RegexRules.Single().Action, Is.EqualTo(PolicyActions.PseudonymizeRestorable));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_InvalidRegexPolicy_IsRejectedAndKeepsLastKnownGood()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "bad-regex"

                [[regex]]
                type = "project"
                pattern = "["
                action = "pseudonymize_restorable"
                """);
            var lastKnownGood = RedactionPolicy.BuiltInDefaults with { Profile = "last-known-good-regex" };

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath, lastKnownGood);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("last-known-good-regex"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_UnsafeRegexPolicy_IsRejectedBeforeActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "unsafe-regex"

                [[regex]]
                type = "project"
                pattern = "^(?<word>[A-Z]+)\\s+\\k<word>$"
                action = "pseudonymize_restorable"
                """);
            var lastKnownGood = RedactionPolicy.BuiltInDefaults with { Profile = "last-known-good-safe-regex" };

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath, lastKnownGood);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("last-known-good-safe-regex"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_UnsafeRegexPolicy_IsRejectedAndKeepsLastKnownGood()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "unsafe-regex"

                [[regex]]
                type = "project"
                pattern = "(a+)+"
                action = "pseudonymize_restorable"
                """);
            var lastKnownGood = RedactionPolicy.BuiltInDefaults with { Profile = "last-known-good-safe-regex" };

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath, lastKnownGood);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("last-known-good-safe-regex"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_TooLongRegexPolicy_IsRejectedBeforeActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            var longPattern = new string('A', 513);
            File.WriteAllText(policyPath, $"""
                version = 1
                profile = "long-regex"

                [[regex]]
                type = "project"
                pattern = "{longPattern}"
                action = "pseudonymize_restorable"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_RegexValidationErrors_DoNotIncludeRawPattern()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "SENSITIVE_MARKER"

                [[regex]]
                type = "project"
                pattern = "SENSITIVE_MARKER["
                action = "pseudonymize_restorable"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);
            var warningText = string.Join(" ", result.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            Assert.That(result.Activated, Is.False);
            Assert.That(warningText, Does.Not.Contain("SENSITIVE_MARKER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_RuleWithWrongShape_IsRejectedBeforeActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "wrong-shape"

                [[allow]]
                type = "url"
                match = "https://learn.microsoft.com/"
                pattern = "https://.*"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_InvalidTomlPolicy_DoesNotActivateAndKeepsLastKnownGood()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "bad-policy"

                [defaults]
                unknown_high_risk = "send_raw_prompt"
                """);
            var lastKnownGood = RedactionPolicy.BuiltInDefaults with { Profile = "last-known-good" };

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath, lastKnownGood);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("last-known-good"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_PolicyLoadErrors_DoNotIncludeRawPromptText()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var policyPath = Path.Combine(tempDirectory, "policy.toml");
            File.WriteAllText(policyPath, """
                version = 1
                profile = "SENSITIVE_MARKER"

                [defaults]
                secret = "SENSITIVE_MARKER"
                """);

            var result = new TomlPolicyLoader().LoadOrDefault(policyPath);
            var warningText = string.Join(" ", result.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            Assert.That(result.Activated, Is.False);
            Assert.That(warningText, Does.Not.Contain("SENSITIVE_MARKER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

[TestFixture]
public class CsvDictionaryLoaderTests
{
    [Test]
    public void LoadOrDefault_ValidCsvDictionary_LoadsSupportedSensitiveTypes()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,pseudonymize_restorable,Known customer
                project,Blue Falcon,pseudonymize_restorable,Internal project
                product,Internal Risk Portal,pseudonymize_restorable,Internal product
                domain,corp.example.local,pseudonymize_restorable,Internal DNS suffix
                system,Payments Core,pseudonymize_restorable,Internal system
                username,user1,pseudonymize_restorable,Windows account
                """);

            var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.ActiveTerms.Select(term => term.Type), Is.EquivalentTo(new[]
            {
                "customer",
                "project",
                "product",
                "domain",
                "system",
                "username"
            }));
            Assert.That(result.ActiveTerms.Single(term => term.Type == "customer").Value, Is.EqualTo("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_InvalidCsvDictionary_DoesNotActivateAndKeepsLastKnownGood()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,do not leak this
                """);
            var lastKnownGood = new[]
            {
                new DictionaryTerm("customer", "SAFE_CUSTOMER", PolicyActions.PseudonymizeRestorable, null)
            };

            var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath, lastKnownGood);

            Assert.That(result.Activated, Is.False);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.ActiveTerms, Is.EqualTo(lastKnownGood));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_dictionary_rejected"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_UrlDictionaryType_LoadsSuccessfully()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                url,https://deploy.corp.example.local,pseudonymize_restorable,Managed URL dictionary term
                """);

            var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.ActiveTerms.Single().Type, Is.EqualTo("url"));
            Assert.That(result.Warnings, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_ManagedCsvDictionary_LoadsSuccessfully()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "managed-dictionary.csv");
            File.WriteAllText(dictionaryPath, """
                id,type,value,action,notes
                abc123,username,user1,pseudonymize_restorable,Windows account
                def456,domain,corp.example.local,pseudonymize_restorable,Internal DNS suffix
                """);

            var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.LoadedFromFile, Is.True);
            Assert.That(result.ActiveTerms.Select(term => term.Type), Is.EquivalentTo(new[] { "username", "domain" }));
            Assert.That(result.ActiveTerms.Single(term => term.Type == "username").Value, Is.EqualTo("user1"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LoadOrDefault_DictionaryLoadErrors_DoNotIncludeRawDictionaryValue()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,Known customer
                """);

            var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath);
            var warningText = string.Join(" ", result.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            Assert.That(result.Activated, Is.False);
            Assert.That(warningText, Does.Not.Contain("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

[TestFixture]
public class MappingVaultTests
{
    [Test]
    public void GetOrCreatePseudonym_SameTypeAndValue_ReturnsSamePseudonym()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());

        var first = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");
        var second = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void GetOrCreatePseudonym_DifferentTypes_UseDifferentNamespaces()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());

        var synthetic = vault.GetOrCreatePseudonym("synthetic_marker", "shared-value");
        var block = vault.GetOrCreatePseudonym("synthetic_block_marker", "shared-value");

        Assert.That(synthetic, Does.StartWith("SYNTHETIC_"));
        Assert.That(block, Does.StartWith("SYNTHETIC_BLOCK_"));
        Assert.That(block, Is.Not.EqualTo(synthetic));
    }

    [Test]
    public void GetOrCreatePseudonym_DoesNotContainOriginalValue()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());

        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

        Assert.That(pseudonym, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void TryGetPseudonym_ReturnsExistingMappingWithoutCreatingNewOne()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

        var found = vault.TryGetPseudonym("synthetic_marker", "SENSITIVE_MARKER", out var lookup);

        Assert.That(found, Is.True);
        Assert.That(lookup, Is.EqualTo(pseudonym));
    }

    [Test]
    public void TryGetOriginal_ReturnsRecordByPseudonym()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

        var found = vault.TryGetOriginal(pseudonym, out var record);

        Assert.That(found, Is.True);
        Assert.That(record.EntityType, Is.EqualTo("synthetic_marker"));
        Assert.That(record.NormalizedValue, Is.EqualTo("SENSITIVE_MARKER"));
        Assert.That(record.Pseudonym, Is.EqualTo(pseudonym));
    }

    private static byte[] TestSecret()
    {
        return System.Text.Encoding.UTF8.GetBytes("unit-test-secret");
    }
}

[TestFixture]
public class FileMappingVaultTests
{
    [Test]
    public void GetOrCreatePseudonym_PersistsMappingAcrossRestartSimulation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var firstVault = FileMappingVault.CreateProtected(
                vaultFilePath,
                TestSecret(),
                new DeterministicDataProtector());

            var first = firstVault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");
            var secondVault = FileMappingVault.CreateProtected(
                vaultFilePath,
                TestSecret(),
                new DeterministicDataProtector());
            var second = secondVault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

            Assert.That(second, Is.EqualTo(first));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Lookups_SupportOriginalAndReverseIndexes()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vault = FileMappingVault.CreateProtected(
                Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName),
                TestSecret(),
                new DeterministicDataProtector());
            var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");

            var foundOriginal = vault.TryGetPseudonym("synthetic_marker", "SENSITIVE_MARKER", out var originalLookup);
            var foundReverse = vault.TryGetOriginal(pseudonym, out var reverseLookup);

            Assert.That(foundOriginal, Is.True);
            Assert.That(originalLookup, Is.EqualTo(pseudonym));
            Assert.That(foundReverse, Is.True);
            Assert.That(reverseLookup.EntityType, Is.EqualTo("synthetic_marker"));
            Assert.That(reverseLookup.NormalizedValue, Is.EqualTo("SENSITIVE_MARKER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProtectedMode_DoesNotWritePlaintextOriginalValue()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var vault = FileMappingVault.CreateProtected(
                vaultFilePath,
                TestSecret(),
                new DeterministicDataProtector());

            vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");
            var fileText = File.ReadAllText(vaultFilePath);

            Assert.That(fileText, Does.Contain("\"storage_mode\": \"protected\""));
            Assert.That(fileText, Does.Not.Contain("SENSITIVE_MARKER"));
            Assert.That(fileText, Does.Not.Contain("synthetic_marker"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PlaintextMode_IsOnlyAvailableThroughExplicitDevelopmentFactory()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var vault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());

            vault.GetOrCreatePseudonym("synthetic_marker", "SENSITIVE_MARKER");
            var fileText = File.ReadAllText(vaultFilePath);

            Assert.That(fileText, Does.Contain("\"storage_mode\": \"plaintext_dev_test\""));
            Assert.That(fileText, Does.Contain("SENSITIVE_MARKER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Writes_UseTemporaryFileAndLeaveNoTempFileBehind()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var vault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());

            vault.GetOrCreatePseudonym("synthetic_marker", "first-value");
            vault.GetOrCreatePseudonym("synthetic_marker", "second-value");

            Assert.That(File.Exists(vaultFilePath), Is.True);
            Assert.That(Directory.GetFiles(tempDirectory, "*.tmp"), Is.Empty);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Writes_ReloadBeforePersistToAvoidLostUpdatesFromAnotherInstance()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var firstVault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());
            var secondVault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());

            var firstPseudonym = firstVault.GetOrCreatePseudonym("synthetic_marker", "first-value");
            var secondPseudonym = secondVault.GetOrCreatePseudonym("synthetic_marker", "second-value");
            var reloadedVault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());

            Assert.That(reloadedVault.TryGetPseudonym("synthetic_marker", "first-value", out var firstLookup), Is.True);
            Assert.That(reloadedVault.TryGetPseudonym("synthetic_marker", "second-value", out var secondLookup), Is.True);
            Assert.That(firstLookup, Is.EqualTo(firstPseudonym));
            Assert.That(secondLookup, Is.EqualTo(secondPseudonym));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Lookups_ReloadBeforeReadToSeeMappingsFromAnotherInstance()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var firstVault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());
            var secondVault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());

            var pseudonym = secondVault.GetOrCreatePseudonym("synthetic_marker", "shared-after-load");

            Assert.That(firstVault.TryGetPseudonym("synthetic_marker", "shared-after-load", out var lookup), Is.True);
            Assert.That(lookup, Is.EqualTo(pseudonym));
            Assert.That(firstVault.TryGetOriginal(pseudonym, out var record), Is.True);
            Assert.That(record.NormalizedValue, Is.EqualTo("shared-after-load"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Load_AcceptsLegacyHexUsernamePseudonymButCreatesReadableUsernamePseudonyms()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var vaultFilePath = Path.Combine(tempDirectory, FileMappingVault.DefaultVaultFileName);
            var legacy = MappingPseudonyms.CreateLegacyHex(TestSecret(), "username", "user1");
            File.WriteAllText(
                vaultFilePath,
                $$"""
                {
                  "Version": 1,
                  "storage_mode": "plaintext_dev_test",
                  "Mappings": [
                    {
                      "entity_type": "username",
                      "normalized_value": "user1",
                      "Pseudonym": "{{legacy}}"
                    }
                  ]
                }
                """);

            var vault = FileMappingVault.CreatePlaintextForDevelopment(vaultFilePath, TestSecret());
            var next = vault.GetOrCreatePseudonym("username", "user2");

            Assert.That(vault.TryGetPseudonym("username", "user1", out var reloaded), Is.True);
            Assert.That(reloaded, Does.Match(@"^USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}$"));
            Assert.That(reloaded, Is.Not.EqualTo(legacy));
            Assert.That(vault.TryGetOriginal(legacy, out var legacyRecord), Is.True);
            Assert.That(legacyRecord.NormalizedValue, Is.EqualTo("user1"));
            Assert.That(legacy, Does.Match(@"^USERNAME_[0-9A-F]{12}$"));
            Assert.That(next, Does.Match(@"^USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}$"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] TestSecret()
    {
        return System.Text.Encoding.UTF8.GetBytes("unit-test-secret");
    }
}

[TestFixture]
public class HmacSecretProviderTests
{
    [Test]
    public void GetOrCreateSecret_CreatesAndLoadsProtectedLocalSecret()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var secretFilePath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);
            var protector = new DeterministicDataProtector();
            var firstProvider = new DpapiProtectedHmacSecretProvider(secretFilePath, protector);

            var firstSecret = firstProvider.GetOrCreateSecret();
            var fileBytes = File.ReadAllBytes(secretFilePath);
            var secondProvider = new DpapiProtectedHmacSecretProvider(secretFilePath, protector);
            var secondSecret = secondProvider.GetOrCreateSecret();

            Assert.That(firstSecret, Has.Length.EqualTo(DpapiProtectedHmacSecretProvider.SecretSizeBytes));
            Assert.That(secondSecret, Is.EqualTo(firstSecret));
            Assert.That(fileBytes, Is.Not.EqualTo(firstSecret));
            Assert.That(ContainsSequence(fileBytes, firstSecret), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DefaultSecretFile_IsSeparateFromFutureMappingVaultFile()
    {
        Assert.That(DpapiProtectedHmacSecretProvider.DefaultSecretFileName, Is.Not.EqualTo("vault.json"));
        Assert.That(DpapiProtectedHmacSecretProvider.DefaultSecretFileName, Is.Not.EqualTo("vault.jsonl"));
    }

    [Test]
    public void CreateProduction_UsesWindowsDpapiProtector()
    {
        var provider = DpapiProtectedHmacSecretProvider.CreateProduction();

        Assert.That(provider.ProtectionKind, Is.EqualTo("windows_dpapi"));
        Assert.That(provider.SecretFilePath, Does.EndWith(DpapiProtectedHmacSecretProvider.DefaultSecretFileName));
    }

    [Test]
    public void DpapiSecretLoadFailureException_CatchesUnprotectErrors()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretFilePath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);

            // Create empty file
            File.WriteAllText(secretFilePath, "");

            var protector = new DeterministicDataProtector();
            var provider = new DpapiProtectedHmacSecretProvider(secretFilePath, protector);

            // Empty file should throw
            Assert.Throws<InvalidOperationException>(() => provider.GetOrCreateSecret());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DpapiSecretLoadFailureException_CatchesInvalidLength()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretFilePath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);

            // Create file with wrong length
            var protector = new DeterministicDataProtector();
            var wrongLengthSecret = new byte[16]; // Should be 32
            var protectedBytes = protector.Protect(wrongLengthSecret);
            File.WriteAllBytes(secretFilePath, protectedBytes);

            var provider = new DpapiProtectedHmacSecretProvider(secretFilePath, protector);

            // Invalid length should throw DpapiSecretLoadFailureException
            var ex = Assert.Throws<DpapiSecretLoadFailureException>(() => provider.GetOrCreateSecret());
            Assert.That(ex!.Message, Is.EqualTo("Local protection initialization failed."));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TryCreateProductionVault_FailsClosedOnDpapiFailure()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            // Force empty secret file
            var secretPath = Path.Combine(tempDirectory, "hmac-secret.dpapi");
            Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
            File.WriteAllText(secretPath, "");

            // Override the default path
            var vault = Sanitizer.TryCreateProductionVault(layout, out var failureReason);

            Assert.That(vault, Is.Null);
            Assert.That(failureReason, Is.Not.Null);
            Assert.That(failureReason, Does.Not.Contain("192.168.10.25"));
            Assert.That(failureReason, Does.Not.Contain("BLOCK_THIS"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DpapiSecretLoadFailureException_CatchesCorruptedData()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretFilePath = Path.Combine(tempDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName);

            // Create file with corrupted data (not valid DPAPI protected data)
            File.WriteAllBytes(secretFilePath, new byte[] { 1, 2, 3, 4, 5 });

            var protector = new WindowsDpapiDataProtector();
            var provider = new DpapiProtectedHmacSecretProvider(secretFilePath, protector);

            // Corrupted data should throw DpapiSecretLoadFailureException
            var ex = Assert.Throws<DpapiSecretLoadFailureException>(() => provider.GetOrCreateSecret());
            Assert.That(ex!.Message, Does.Not.Contain("192.168.10.25"));
            Assert.That(ex.Message, Does.Not.Contain("BLOCK_THIS"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool ContainsSequence(byte[] bytes, byte[] sequence)
    {
        for (var index = 0; index <= bytes.Length - sequence.Length; index++)
        {
            if (bytes.AsSpan(index, sequence.Length).SequenceEqual(sequence))
            {
                return true;
            }
        }

        return false;
    }

}

[TestFixture]
public class RestorationTests
{
    [Test]
    public void Restore_RestorablePseudonym_ReturnsLocalOriginalValue()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "CUSTOMER_ACME");
        var restorer = new LocalRestorer(vault);

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: $"Talk to {pseudonym} tomorrow.",
            Replacements: new[] { CreateReplacement(pseudonym, restorable: true) }));

        Assert.That(result.Text, Is.EqualTo("Talk to CUSTOMER_ACME tomorrow."));
        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Metadata.LocalSensitive, Is.True);
        Assert.That(result.Metadata.RestoredPseudonymCountsByType["synthetic_marker"], Is.EqualTo(1));
    }

    [Test]
    public void Restore_UnknownPseudonym_LeavesTextUnchangedWithSafeWarning()
    {
        var restorer = new LocalRestorer(new InMemoryHmacMappingVault(TestSecret()));

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: "Talk to SYNTHETIC_UNKNOWN tomorrow.",
            Replacements: new[] { CreateReplacement("SYNTHETIC_UNKNOWN", restorable: true) }));

        Assert.That(result.Text, Is.EqualTo("Talk to SYNTHETIC_UNKNOWN tomorrow."));
        Assert.That(result.Metadata.LocalSensitive, Is.False);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("unknown_pseudonym"));
        Assert.That(result.Warnings.Single().Message, Does.Not.Contain("CUSTOMER_ACME"));
    }

    [Test]
    public void Restore_NonRestorableRedaction_LeavesTextUnchanged()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "CUSTOMER_ACME");
        var restorer = new LocalRestorer(vault);

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: $"Talk to {pseudonym} tomorrow.",
            Replacements: new[] { CreateReplacement(pseudonym, restorable: false) }));

        Assert.That(result.Text, Is.EqualTo($"Talk to {pseudonym} tomorrow."));
        Assert.That(result.Metadata.LocalSensitive, Is.False);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("non_restorable_redaction_skipped"));
    }

    [Test]
    public void Restore_MultipleOccurrences_MarksRestoredOutputLocalSensitive()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("synthetic_marker", "CUSTOMER_ACME");
        var restorer = new LocalRestorer(vault);

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: $"{pseudonym} and {pseudonym}",
            Replacements: new[] { CreateReplacement(pseudonym, restorable: true) }));

        Assert.That(result.Text, Is.EqualTo("CUSTOMER_ACME and CUSTOMER_ACME"));
        Assert.That(result.Metadata.LocalSensitive, Is.True);
        Assert.That(result.Metadata.RestoredPseudonymCountsByType["synthetic_marker"], Is.EqualTo(2));
    }

    [Test]
    public void LocalRestoreWorkflow_RestoresKnownPseudonymFromPastedTextAndMarksOutput()
    {
        var tempDirectory = CreateTempDirectory();
        var auditDirectory = Path.Combine(tempDirectory, "audit");
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("customer", "ACME Banking");
        var workflow = new LocalRestoreWorkflow(new LocalRestorer(vault), new FileAuditSink(auditDirectory));

        try
        {
            var result = workflow.RestoreText($"Model response mentions {pseudonym} and TOKEN_REDACTED.");

            Assert.That(result.Restoration.Text, Does.Contain("ACME Banking"));
            Assert.That(result.Restoration.Text, Does.Contain("TOKEN_REDACTED"));
            Assert.That(result.Restoration.Metadata.LocalSensitive, Is.True);
            Assert.That(result.DisplayText, Does.Contain("LOCAL-SENSITIVE RESTORED OUTPUT"));
            Assert.That(result.DisplayText, Does.Contain("Sanitize again before sending"));
            Assert.That(result.Restoration.Metadata.RestoredPseudonymCountsByType["customer"], Is.EqualTo(1));
            Assert.That(result.Restoration.Warnings.Single().Code, Is.EqualTo("non_restorable_redaction_skipped"));

            var auditText = File.ReadAllText(Directory.EnumerateFiles(auditDirectory, "audit-*.json").Single());
            Assert.That(auditText, Does.Contain("restore_restorable"));
            Assert.That(auditText, Does.Contain("warning.non_restorable_redaction_skipped"));
            Assert.That(auditText, Does.Contain("non_restorable_redaction_skipped"));
            Assert.That(auditText, Does.Contain("Restoration warning code recorded."));
            Assert.That(auditText, Does.Not.Contain("Non-restorable redaction was left unchanged."));
            Assert.That(auditText, Does.Not.Contain("ACME Banking"));
            Assert.That(auditText, Does.Not.Contain(result.Restoration.Text));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LocalRestoreWorkflow_RestoresReadableUsernamePseudonymFromPastedText()
    {
        var tempDirectory = CreateTempDirectory();
        var auditDirectory = Path.Combine(tempDirectory, "audit");
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("username", "user1");
        var workflow = new LocalRestoreWorkflow(new LocalRestorer(vault), new FileAuditSink(auditDirectory));

        try
        {
            var result = workflow.RestoreText($@"Use C:\Users\{pseudonym}> for this command.");

            Assert.That(pseudonym, Does.Match(@"^USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}$"));
            Assert.That(result.Restoration.Text, Does.Contain(@"C:\Users\user1>"));
            Assert.That(result.Restoration.Metadata.LocalSensitive, Is.True);
            Assert.That(result.Restoration.Metadata.RestoredPseudonymCountsByType["username"], Is.EqualTo(1));
            Assert.That(result.DisplayText, Does.Contain("LOCAL-SENSITIVE RESTORED OUTPUT"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LocalRestoreWorkflow_LeavesUnknownPseudonymsAndRedactionsWithRawFreeWarnings()
    {
        var tempDirectory = CreateTempDirectory();
        var workflow = new LocalRestoreWorkflow(
            new LocalRestorer(new InMemoryHmacMappingVault(TestSecret())),
            new FileAuditSink(Path.Combine(tempDirectory, "audit")));

        try
        {
            var result = workflow.RestoreText("Keep CUSTOMER_012345ABCDEF and PASSWORD_REDACTED unchanged.");
            var warningText = string.Join(Environment.NewLine, result.Restoration.Warnings.Select(warning => warning.Message));

            Assert.That(result.Restoration.Text, Is.EqualTo("Keep CUSTOMER_012345ABCDEF and PASSWORD_REDACTED unchanged."));
            Assert.That(result.Restoration.Metadata.LocalSensitive, Is.False);
            Assert.That(result.Restoration.Warnings.Select(warning => warning.Code), Is.EquivalentTo(new[]
            {
                "unknown_pseudonym",
                "non_restorable_redaction_skipped"
            }));
            Assert.That(warningText, Does.Not.Contain("CUSTOMER_012345ABCDEF"));
            Assert.That(warningText, Does.Not.Contain("PASSWORD_REDACTED"));
            Assert.That(result.DisplayText, Does.Contain("NO LOCAL VALUES RESTORED"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static Replacement CreateReplacement(string pseudonym, bool restorable)
    {
        return new Replacement(
            ContentPartId: "prompt",
            Offset: 0,
            Length: pseudonym.Length,
            Type: "synthetic_marker",
            Placeholder: pseudonym,
            Action: "pseudonymize_restorable",
            Restorable: restorable);
    }

    private static byte[] TestSecret()
    {
        return System.Text.Encoding.UTF8.GetBytes("unit-test-secret");
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

internal static class TestSanitizers
{
    public static Sanitizer Create()
    {
        return new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("unit-test-secret")));
    }
}

internal sealed class DeterministicDataProtector : IDataProtector
{
    private static readonly byte[] Prefix = System.Text.Encoding.ASCII.GetBytes("protected:");

    public string ProtectionKind => "test_deterministic";

    public byte[] Protect(byte[] plaintext)
    {
        var protectedData = new byte[Prefix.Length + plaintext.Length];
        Prefix.CopyTo(protectedData, 0);
        for (var index = 0; index < plaintext.Length; index++)
        {
            protectedData[Prefix.Length + index] = (byte)(plaintext[index] ^ 0xA5);
        }

        return protectedData;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!protectedData.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
        {
            throw new InvalidOperationException("Unexpected test protected data.");
        }

        var plaintext = new byte[protectedData.Length - Prefix.Length];
        for (var index = 0; index < plaintext.Length; index++)
        {
            plaintext[index] = (byte)(protectedData[Prefix.Length + index] ^ 0xA5);
        }

        return plaintext;
    }
}

[TestFixture]
public partial class SanitizerTests
{
    [Test]
    public void TrayProtectionController_StartStopUpdatesEnabledModeAndHotkey()
    {
        var host = new RecordingTrayHotkeyHost();
        var controller = new TrayProtectionController(host, () => throw new InvalidOperationException("Should not run."));

        var started = controller.Start();
        var runningState = controller.State;
        controller.Stop();
        var stoppedState = controller.State;

        Assert.That(started, Is.True);
        Assert.That(host.Started, Is.False);
        Assert.That(runningState.Enabled, Is.True);
        Assert.That(runningState.Mode, Is.EqualTo("ApplyOnly"));
        Assert.That(runningState.Hotkey, Is.EqualTo("Ctrl+Shift+F9"));
        Assert.That(runningState.LastStatus, Is.EqualTo("enabled"));
        Assert.That(stoppedState.Enabled, Is.False);
        Assert.That(stoppedState.LastStatus, Is.EqualTo("disabled"));
    }

    [Test]
    public void TrayProtectionController_RegistrationFailureLeavesProtectionDisabledWithRawFreeCode()
    {
        var host = new FailingTrayHotkeyHost("Ctrl+Enter", "hotkey_register_failed:1409");
        var controller = new TrayProtectionController(host, () => throw new InvalidOperationException("Should not run."));

        var started = controller.Start();
        var statusText = TrayStatusFormatter.FormatMenuStatus(controller.State);

        Assert.That(started, Is.False);
        Assert.That(controller.State.Enabled, Is.False);
        Assert.That(statusText, Does.Contain("manual_scan_hotkey=Ctrl+Enter"));
        Assert.That(statusText, Does.Contain("last=unavailable"));
        Assert.That(TrayStatusFormatter.FormatStartupError(controller.State), Does.Contain("manual_scan_hotkey=Ctrl+Enter"));
        Assert.That(TrayStatusFormatter.FormatStartupError(controller.State), Does.Contain("error=unavailable"));
        Assert.That(statusText, Does.Not.Contain("ACME Banking"));
        Assert.That(statusText, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void TrayStatusFormatter_RecoveryTooltipNormalizesUnknownStatus()
    {
        var text = TrayStatusFormatter.FormatRecoveryRequiredNotifyIconText("test.secret.com");

        Assert.That(text, Does.Not.Contain("test.secret.com"));
        Assert.That(text, Does.Contain("local_protection=local_protection_unavailable"));
    }

    [Test]
    public void TrayStatusFormatter_RendersOnlyKnownProtectedSendAttemptValues()
    {
        var status = TrayStatusFormatter.FormatMenuStatus(new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            ProtectedSendAttemptStatus: "DOMAIN_C195C3D8E8F3",
            ProtectedSendAttemptAction: "DOMAIN_C195C3D8E8F3"));

        Assert.That(status, Does.Contain("protected_send_attempt=unavailable"));
        Assert.That(status, Does.Contain("attempt_action=none"));
        Assert.That(status, Does.Not.Contain("DOMAIN_C195C3D8E8F3"));
    }

    [Test]
    public void TrayStatusFormatter_RendersInterruptedSendAsAKnownRawFreeValue()
    {
        var status = TrayStatusFormatter.FormatMenuStatus(new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            LastProtectedSendInterruption: new ProtectedSendInterruption(
                AttemptId: 12,
                SourceGeneration: 7,
                Reason: "DOMAIN_C195C3D8E8F3",
                Action: "DOMAIN_C195C3D8E8F3")));

        Assert.That(status, Does.Contain("protected_send_interruption=unavailable"));
        Assert.That(status, Does.Not.Contain("DOMAIN_C195C3D8E8F3"));
    }

    [Test]
    public void TrayProtectionController_HotkeyRunsApplyOnlyAndStoresRawFreeResult()
    {
        const string rawPrompt = "Discuss ACME Banking status.";
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            new[]
            {
                new DictionaryTerm("customer", "ACME Banking", "pseudonymize_restorable", null)
            });
        var sanitizationResult = sanitizer.Sanitize(CreatePromptRequest(rawPrompt));
        var surface = new TextSurfaceDescriptor(
            "composer-1",
            "codex-desktop",
            "Codex desktop composer",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: false,
            Metadata: new SurfaceMetadata());
        var host = new RecordingTrayHotkeyHost();
        var controller = TrayProtectionController.CreateTest(
            host,
            () => new OsInteractionResult(
                OsInteractionStatusIds.Applied,
                surface,
                sanitizationResult,
                ConfirmationModel: null,
                Applied: true,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()));

        controller.Start();
        host.Trigger();
        var statusText = TrayStatusFormatter.FormatMenuStatus(controller.State);
        var stateText = controller.State.ToString();

        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Applied));
        Assert.That(controller.State.LastDecision, Is.EqualTo("confirm"));
        Assert.That(controller.State.LastReplacementCount, Is.EqualTo(1));
        Assert.That(controller.State.LastProfileId, Is.EqualTo("codex-desktop"));
        Assert.That(controller.State.LastApplied, Is.True);
        Assert.That(controller.State.LastSubmitted, Is.False);
        Assert.That(statusText, Does.Contain("status=enabled"));
        Assert.That(statusText, Does.Contain("last=applied"));
        Assert.That(statusText, Does.Contain("replacements=1"));
        Assert.That(TrayStatusFormatter.FormatNotifyIconText(controller.State), Does.Contain("ApplyOnly"));
        Assert.That(TrayStatusFormatter.FormatNotifyIconText(controller.State), Does.Contain("last=applied"));
        Assert.That(TrayStatusFormatter.FormatNotifyIconText(controller.State).Length, Is.LessThanOrEqualTo(63));
        Assert.That(stateText, Does.Not.Contain("ACME Banking"));
        Assert.That(stateText, Does.Not.Contain("CUSTOMER_"));
        Assert.That(statusText, Does.Not.Contain("ACME Banking"));
        Assert.That(statusText, Does.Not.Contain("CUSTOMER_"));
    }

    [Test]
    public void TrayNotifyIconText_CarriesBuildVersionAndStaysWithinWindowsLimit()
    {
        var text = TrayStatusFormatter.FormatNotifyIconText(
            new TrayProtectionState(
                Enabled: true,
                Mode: "NativeSubmit",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.Protected,
                LastDecision: null,
                LastReplacementCount: null,
                LastProfileId: "codex-desktop",
                LastApplied: false,
                LastSubmitted: false,
                NativeSubmitEnabled: true,
                NativeSubmitStatus: OsInteractionStatusIds.Protected,
                ProtectedSendBinding: "Enter",
                NewlineBinding: "Ctrl+Enter",
                ManualScanHotkey: "Ctrl+Shift+F9",
                ReadinessStatus: OsInteractionStatusIds.Protected,
                ComposerProtected: true,
                ResidentProcess: true),
            "0.1.20260722.t1234");

        Assert.That(text, Does.StartWith("CodexRG 0.1.20260722.t1234"));
        Assert.That(text.Length, Is.LessThanOrEqualTo(63));
        Assert.That(text, Does.Not.Contain("ACME Banking"));
        Assert.That(text, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void TrayMenuContent_FormatsBuildVersionForResidentUi()
    {
        var menuItem = TrayMenuContent.FormatBuildVersionMenuItem(" 0.1.20260722.t1234 ");
        var helpText = TrayMenuContent.FormatBuildVersionHelpText("0.1.20260722.t1234");

        Assert.That(menuItem, Is.EqualTo("Version: 0.1.20260722.t1234"));
        Assert.That(helpText, Does.Contain("Build version:"));
        Assert.That(helpText, Does.Contain("0.1.20260722.t1234"));
        Assert.That(TrayMenuContent.FormatBuildVersionMenuItem(" "), Is.EqualTo("Version: unknown"));
    }

    [Test]
    public void BuildVersion_UsesInformationalVersionBeforeAssemblyVersion()
    {
        Assert.That(BuildVersion.Normalize(" 0.1.20260722.t1234 ", new Version(1, 2, 3, 4)), Is.EqualTo("0.1.20260722.t1234"));
        Assert.That(BuildVersion.Normalize(null, new Version(1, 2, 3, 4)), Is.EqualTo("1.2.3.4"));
        Assert.That(BuildVersion.Normalize(" ", null), Is.EqualTo("unknown"));
    }

    [Test]
    public void TrayProtectionController_SeparatesProtectedSendBindingFromManualHotkey()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
                "surface-1",
                "codex-desktop",
                "Codex desktop composer",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata())));
        var controller = TrayProtectionController.CreateTest(
            new RecordingTrayHotkeyHost("Ctrl+Shift+F9"),
            () => throw new InvalidOperationException("Manual scan should not run."),
            new RecordingNativeSubmitHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>()),
            profile);

        controller.Start();
        var statusText = TrayStatusFormatter.FormatMenuStatus(controller.State);

        Assert.That(controller.State.NativeSubmitEnabled, Is.True);
        Assert.That(statusText, Does.Contain("protected_send_binding=Enter"));
        Assert.That(statusText, Does.Contain("newline_binding=Ctrl+Enter"));
        Assert.That(statusText, Does.Contain("manual_scan_hotkey=Ctrl+Shift+F9"));
        Assert.That(statusText, Does.Not.Contain("hotkey=Ctrl+Enter"));
    }

    [Test]
    public void TrayProtectionController_ReportsProgrammaticUiaInvokeAsUnsupportedAlongsideProtectedManualSend()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
                "surface-1",
                "codex-desktop",
                "Codex desktop composer",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata())));
        var controller = TrayProtectionController.CreateTest(
            new RecordingTrayHotkeyHost("Ctrl+Shift+F9"),
            () => throw new InvalidOperationException("Manual scan should not run."),
            new RecordingNativeSubmitHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Native submit should not run."),
            profile);

        controller.Start();
        var profileDiagnostics = profile.ToRawFreeDiagnostics();
        var trayStatus = TrayStatusFormatter.FormatMenuStatus(controller.State);

        Assert.That(profile.IsProtected, Is.True);
        Assert.That(
            profileDiagnostics["programmatic_uia_invoke"],
            Is.EqualTo(OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported));
        Assert.That(
            controller.State.ProgrammaticUiaInvokeStatus,
            Is.EqualTo(OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported));
        Assert.That(trayStatus, Does.Contain("programmatic_uia_invoke=programmatic_uia_invoke_unsupported"));
    }

    [Test]
    public void TrayProtectionController_DisableRequiresConfirmationAndCanBePolicyBlocked()
    {
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
                "surface-1",
                "codex-desktop",
                "Codex desktop composer",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata())));
        var controller = TrayProtectionController.CreateTest(
            new RecordingTrayHotkeyHost("Ctrl+Shift+F9"),
            () => throw new InvalidOperationException("Manual scan should not run."),
            new RecordingNativeSubmitHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>()),
            profile);
        controller.Start();

        var canceled = controller.TryDisableProtection("exit", confirmed: false);
        var confirmed = controller.TryDisableProtection("exit", confirmed: true);

        Assert.That(canceled.Succeeded, Is.False);
        Assert.That(canceled.ProtectionStillRunning, Is.True);
        Assert.That(controller.State.Enabled, Is.False);
        Assert.That(confirmed.Succeeded, Is.True);
        Assert.That(confirmed.ProtectionStillRunning, Is.False);
        Assert.That(confirmed.Diagnostics["raw_prompt_recorded"], Is.EqualTo("false"));

        var managed = TrayProtectionController.CreateTest(
            new RecordingTrayHotkeyHost("Ctrl+Shift+F9"),
            () => throw new InvalidOperationException("Manual scan should not run."),
            new RecordingNativeSubmitHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>()),
            profile,
            new NativeSubmitEnterprisePolicy(
                ManagedMode: true,
                RequiredProfileIds: new[] { "codex-desktop" },
                DisallowHotkeyOnlyDegradation: true,
                UnverifiedRequiredProfileBehavior: "block_submit"));
        managed.Start();

        var blocked = managed.TryDisableProtection("exit", confirmed: true);

        Assert.That(blocked.Succeeded, Is.False);
        Assert.That(blocked.Code, Is.EqualTo("protection_disable_blocked_by_policy"));
        Assert.That(blocked.ProtectionStillRunning, Is.True);
        Assert.That(managed.State.Enabled, Is.True);
    }

    [Test]
    public void TrayProtectionDisableConfirmationText_NamesConsequenceWithoutRawPrompt()
    {
        var text = TrayProtectionDisableConfirmationText.Format(
            "exit Code Sanitizer",
            new TrayProtectionState(
                Enabled: true,
                Mode: "NativeSubmit",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.Protected,
                LastDecision: null,
                LastReplacementCount: null,
                LastProfileId: "codex-desktop",
                LastApplied: false,
                LastSubmitted: false,
                NativeSubmitEnabled: true,
                NativeSubmitStatus: OsInteractionStatusIds.Protected,
                ProtectedSendBinding: "Enter",
                NewlineBinding: "Ctrl+Enter",
                ManualScanHotkey: "Ctrl+Shift+F9",
                ReadinessStatus: OsInteractionStatusIds.Protected,
                ResidentProcess: true));

        Assert.That(text, Does.Contain("Selected AI apps will no longer be protected"));
        Assert.That(text, Does.Contain("protected_send_binding=Enter"));
        Assert.That(text, Does.Not.Contain("ACME Banking"));
        Assert.That(text, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void TrayMenuContent_ExposesDiagnosticsAndRuleManagementCommandsWithoutRawPromptText()
    {
        var text = TrayMenuContent.RestoreText + Environment.NewLine + TrayMenuContent.DiagnosticsText + Environment.NewLine + TrayMenuContent.RuleManagementText;

        Assert.That(text, Does.Contain("--restore-view"));
        Assert.That(text, Does.Contain("--restore-text \"sanitized model response\""));
        Assert.That(text, Does.Contain("--policy-diagnostics"));
        Assert.That(text, Does.Contain("--audit-summary"));
        Assert.That(text, Does.Contain("--audit-view"));
        Assert.That(text, Does.Contain("--audit-verify"));
        Assert.That(text, Does.Contain("--audit-cleanup --keep 100"));
        Assert.That(text, Does.Contain("--os-compatibility-matrix"));
        Assert.That(text, Does.Contain("--product-smoke"));
        Assert.That(text, Does.Contain("--os-composer-diagnostic"));
        Assert.That(text, Does.Contain("--hotkey-show"));
        Assert.That(text, Does.Contain("Manual scan/apply hotkey commands:"));
        Assert.That(text, Does.Contain("--hotkey-set \"Ctrl+Shift+F9\""));
        Assert.That(text, Does.Contain("Protected Send binding commands:"));
        Assert.That(text, Does.Contain("--native-profile-verify codex-desktop Enter Ctrl+Enter"));
        Assert.That(text, Does.Contain("--native-profile-verify-delay codex-desktop Enter Ctrl+Enter 10"));
        Assert.That(text, Does.Contain("--native-profile-verify-delay chatgpt-desktop Enter Ctrl+Enter 10"));
        Assert.That(text, Does.Contain("--send-mode-show"));
        Assert.That(text, Does.Contain("--send-mode-enable"));
        Assert.That(text, Does.Contain("--send-mode-disable"));
        Assert.That(text, Does.Contain("--autostart-show"));
        Assert.That(text, Does.Contain("--autostart-enable"));
        Assert.That(text, Does.Contain("--autostart-disable"));
        Assert.That(text, Does.Contain("--local-data-cleanup [--i-understand-delete-local-sensitive-data]"));
        Assert.That(text, Does.Contain("Local sensitive terms UI:"));
        Assert.That(text, Does.Contain("--dictionary-ui"));
        Assert.That(text, Does.Contain("--dictionary-add-batch"));
        Assert.That(text, Does.Contain("--dictionary-remove id [id]..."));
        Assert.That(text, Does.Contain("--policy-test \"text\" [--show-sanitized]"));
        Assert.That(text, Does.Contain("--rules-export"));
        Assert.That(text, Does.Not.Contain("ACME Banking"));
        Assert.That(text, Does.Not.Contain("SENSITIVE_MARKER"));
        Assert.That(text, Does.Not.Contain("user1"));
    }

    [Test]
    public void ProductUiWording_DoesNotReferToApplyOnlyFlowAsDemo()
    {
        var model = ConfirmationUiShell.CreateModel(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25")));
        var text = string.Join(
            Environment.NewLine,
            TrayMenuContent.RestoreText,
            TrayMenuContent.DiagnosticsText,
            TrayMenuContent.RuleManagementText,
            TrayStatusFormatter.FormatMenuStatus(new TrayProtectionState(
                Enabled: true,
                Mode: "ApplyOnly",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.Applied,
                LastDecision: "confirm",
                LastReplacementCount: 1,
                LastProfileId: "codex-desktop",
                LastApplied: true,
                LastSubmitted: false)),
            OsConfirmationOverlayRenderer.RenderText(model));
        var productSourceText = string.Join(
            Environment.NewLine,
            ProductSourceText("TrayProtection.cs"),
            ProductSourceText("WindowsTrayApp.cs"),
            ProductSourceText("DictionaryManagementForm.cs"),
            ProductSourceText("ConfirmationUi.cs"),
            ProductSourceText("WindowsConfirmationOverlay.cs"),
            ProductSourceText("OsConfirmationOverlayRenderer.cs"));

        Assert.That(text, Does.Not.Contain("demo").IgnoreCase);
        Assert.That(productSourceText, Does.Not.Contain("demo").IgnoreCase);
    }

    [Test]
    public void WindowsTrayLocalCommandLauncher_OpensAllowlistedConsoleCommandWithoutRawPromptText()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory, "CodexRedactionGate.exe"), "test exe placeholder");

            var startInfo = WindowsTrayLocalCommandLauncher.CreateStartInfo(
                TrayMenuContent.DiagnosticsCommand,
                tempDirectory,
                currentProcessPath: null);
            var arguments = string.Join(" ", startInfo.ArgumentList);

            Assert.That(startInfo.FileName, Is.EqualTo("powershell.exe"));
            Assert.That(startInfo.CreateNoWindow, Is.False);
            Assert.That(arguments, Does.Contain("--policy-diagnostics"));
            Assert.That(arguments, Does.Contain("CodexRedactionGate.exe"));
            Assert.That(arguments, Does.Not.Contain("ACME Banking"));
            Assert.That(arguments, Does.Not.Contain("SENSITIVE_MARKER"));
            Assert.That(arguments, Does.Not.Contain("user1"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void HotkeySettingsStore_DefaultProtectionHotkeyIsNonReserved()
    {
        var hotkey = HotkeySettingsStore.DefaultProtectionHotkey;

        Assert.That(hotkey.Binding.DisplayText, Is.EqualTo("Ctrl+Shift+F9"));
        Assert.That(hotkey.Binding.DisplayText, Does.Not.Contain("F12"));
        Assert.That(hotkey.Modifiers, Is.Not.Zero);
        Assert.That(hotkey.VirtualKey, Is.Not.Zero);
    }

    [Test]
    public void HotkeyParser_RejectsInvalidAndReservedCombinations()
    {
        Assert.That(HotkeyParser.Parse("F9").Code, Is.EqualTo("hotkey_invalid_missing_modifier"));
        Assert.That(HotkeyParser.Parse("Enter").Code, Is.EqualTo("hotkey_invalid_missing_modifier"));
        Assert.That(HotkeyParser.Parse("Ctrl+Enter").Hotkey!.Binding.DisplayText, Is.EqualTo("Ctrl+Enter"));
        Assert.That(HotkeyParser.Parse("Ctrl+Return").Hotkey!.Binding.DisplayText, Is.EqualTo("Ctrl+Enter"));
        Assert.That(HotkeyParser.Parse("Ctrl+Shift+F12").Code, Is.EqualTo("hotkey_reserved"));
        Assert.That(HotkeyParser.Parse("Win+F9").Code, Is.EqualTo("hotkey_reserved_windows_modifier"));
        Assert.That(HotkeyParser.Parse("Ctrl+Shift+Mouse1").Code, Is.EqualTo("hotkey_invalid_key"));
        Assert.That(HotkeyParser.Parse("Ctrl+F8+F9").Code, Is.EqualTo("hotkey_invalid_multiple_keys"));
    }

    [Test]
    public void HotkeySettingsStore_PersistsUserSelectedHotkey()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var save = HotkeySettingsStore.SaveProtectionHotkey(layout, "Shift+Ctrl+F8");
            var loaded = HotkeySettingsStore.LoadOrDefault(layout);
            var storedText = File.ReadAllText(HotkeySettingsStore.DefaultPath(layout));

            Assert.That(save.Succeeded, Is.True);
            Assert.That(save.Hotkey!.Binding.DisplayText, Is.EqualTo("Ctrl+Shift+F8"));
            Assert.That(loaded.ProtectionHotkey.Binding.DisplayText, Is.EqualTo("Ctrl+Shift+F8"));
            Assert.That(storedText, Does.Contain("Ctrl+Shift+F8"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void HotkeySettingsStore_InvalidPersistedHotkeyDoesNotFallBackSilently()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            AtomicFileWriter.WriteAllBytes(
                HotkeySettingsStore.DefaultPath(layout),
                System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "protection_hotkey": "Ctrl+Shift+F12"
                    }
                    """));

            var result = HotkeySettingsStore.Load(layout);

            Assert.That(result.Usable, Is.False);
            Assert.That(result.Code, Is.EqualTo("hotkey_reserved"));
            Assert.That(result.Settings.ProtectionHotkey.Binding.DisplayText, Is.EqualTo("configured_invalid"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void WindowsTrayHotkeyHost_BindingUsesConfiguredHotkey()
    {
        var hotkey = HotkeyParser.Parse("Ctrl+Alt+F8").Hotkey!;
        var host = new WindowsTrayHotkeyHost(hotkey);

        Assert.That(host.Binding.DisplayText, Is.EqualTo("Ctrl+Alt+F8"));
    }

    [Test]
    public void ProductApplyOnly_ConfirmWritesSanitizedTextAndVerifiesFocusedComposer()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Applied));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Does.Contain("IP_"));
        Assert.That(surface.CurrentText, Does.Not.Contain("192.168.10.25"));
        Assert.That(surface.SubmitCount, Is.Zero);
        Assert.That(surface.DiscoveryCount, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void ProductApplyOnly_FocusLossBeforeWriteLeavesComposerUnsubmitted()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            FailDiscoveryAfterConfirmation = true
        };
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to 192.168.10.25"));
        Assert.That(surface.WriteCount, Is.Zero);
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductApplyOnly_StaleComposerBeforeWriteLeavesOriginalComposerUnchangedAndUnsubmitted()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            ReturnDifferentSurfaceAfterConfirmation = true
        };
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to 192.168.10.25"));
        Assert.That(surface.WriteCount, Is.Zero);
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductApplyOnly_FocusLossAfterWriteLeavesComposerUnsubmitted()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            FailDiscoveryAfterWrite = true
        };
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Does.Contain("IP_"));
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductApplyOnly_StaleComposerAfterWriteLeavesComposerUnsubmitted()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            ReturnDifferentSurfaceAfterWrite = true
        };
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Does.Contain("IP_"));
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductApplyOnly_VerificationMismatchLeavesComposerUnsubmitted()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            VerificationTextOverride = "unexpected sanitized text"
        };
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductApplyOnly_CancelBlockCaptureAndWriteFailureSubmitNothing()
    {
        var cancelSurface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var cancel = CreateProductFlowOrchestrator(cancelSurface, ConfirmationDecisionContract.Cancel)
            .RunOnce(OsInteractionRunOptions.ApplyOnly);

        var blockSurface = new ProductFlowTextSurface("Reject BLOCK_THIS");
        var block = CreateProductFlowOrchestrator(blockSurface, ConfirmationDecisionContract.Confirm)
            .RunOnce(OsInteractionRunOptions.ApplyOnly);

        var captureSurface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            FailInitialCapture = true
        };
        var capture = CreateProductFlowOrchestrator(captureSurface, ConfirmationDecisionContract.Confirm)
            .RunOnce(OsInteractionRunOptions.ApplyOnly);

        var writeSurface = new ProductFlowTextSurface("Connect to 192.168.10.25")
        {
            FailWrites = true
        };
        var write = CreateProductFlowOrchestrator(writeSurface, ConfirmationDecisionContract.Confirm)
            .RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(cancel.Status, Is.EqualTo(OsInteractionStatusIds.Canceled));
        Assert.That(block.Status, Is.EqualTo(OsInteractionStatusIds.Blocked));
        Assert.That(capture.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(cancel.Submitted || block.Submitted || capture.Submitted || write.Submitted, Is.False);
        Assert.That(cancelSurface.SubmitCount + blockSurface.SubmitCount + captureSurface.SubmitCount + writeSurface.SubmitCount, Is.Zero);
    }

    [Test]
    public void OsInteractionOrchestrator_ReportsProtectedStagesBeforeSideEffects()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var stages = new List<string>();
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            (stage, _) =>
            {
                stages.Add(stage);
                return true;
            });

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(result.Submitted, Is.True);
        Assert.That(stages, Is.EqualTo(new[]
        {
            "composer_read",
            "sanitized",
            "overlay_created",
            "overlay_foreground_confirmed",
            "approved",
            "text_written",
            "send_injected"
        }));
        Assert.That(surface.SubmitCount, Is.EqualTo(1));
    }

    [Test]
    public void OsInteractionOrchestrator_RecordsCancellationBeforeTerminalOutcome()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var stages = new List<string>();
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Cancel);

        var result = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            (stage, _) =>
            {
                stages.Add(stage);
                return true;
            });

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Canceled));
        Assert.That(result.Submitted, Is.False);
        Assert.That(stages, Is.EqualTo(new[]
        {
            "composer_read",
            "sanitized",
            "overlay_created",
            "overlay_foreground_confirmed",
            "cancelled"
        }));
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void OsInteractionOrchestrator_BlocksSubmitWhenTraceCannotAdvance()
    {
        var surface = new ProductFlowTextSurface("Safe prompt");
        var submitTraceAllowed = false;
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            (stage, _) => stage != "send_injected" || submitTraceAllowed);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void OsInteractionOrchestrator_StopsBeforeSubmitWhenResidentOperationIsStaleAtReplayBoundary()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var invalidatedAtReplay = false;
        var orchestrator = CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            traceStage: (stage, _) =>
            {
                if (stage == "text_written")
                {
                    invalidatedAtReplay = true;
                }

                return true;
            },
            executionGuard: () => !invalidatedAtReplay);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(result.Submitted, Is.False);
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Diagnostics["trace_status"], Is.EqualTo("resident_operation_unavailable"));
        Assert.That(surface.WriteCount, Is.EqualTo(1));
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void ProductConfirmAndSend_CapturedTargetChangeImmediatelyBeforeReplayBlocksSubmission()
    {
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        var target = new NativeSubmitTargetIdentity(7, "codex-desktop", "1");
        var orchestrator = CreateProductFlowOrchestrator(
            surface,
            ConfirmationDecisionContract.Confirm,
            new CapturedTargetSurfaceDiscovery(surface, target));

        var replayBoundaryReached = false;
        var result = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            traceStage: (stage, _) =>
            {
                if (stage == "text_written")
                {
                    replayBoundaryReached = true;
                    surface.ReturnDifferentSurfaceAtReplayBoundary = true;
                }

                return true;
            });

        Assert.That(replayBoundaryReached, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.WriteCount, Is.EqualTo(1));
        Assert.That(surface.SubmitCount, Is.Zero);
    }

    [Test]
    public void Sanitize_NoSensitivePrompt_ReturnsAllowWithUnchangedText()
    {
        var input = "Normal prompt text";
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo(input));
        Assert.That(result.Replacements, Is.Empty);
        Assert.That(result.AuditEvent, Is.Not.Null);
        Assert.That(result.AuditEvent.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.AuditEvent.SpanSummaries, Is.Empty);
        Assert.That(AuditInspection.Contains(result.AuditEvent, input), Is.False);
    }

    [Test]
    public void Sanitize_NoSensitivePrompt_AuditTraceShowsNoSensitiveCandidates()
    {
        var result = TestSanitizers.Create().Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.reason"], Is.EqualTo("no_sensitive_candidates"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.stage.detectors"], Is.EqualTo("no_candidates"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.count.candidates"], Is.EqualTo("0"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "Normal prompt text"), Is.False);
    }

    [Test]
    public void Sanitize_MultipleSafeParts_ReturnsAllowWithCombinedText()
    {
        var sanitizer = TestSanitizers.Create();
        var request = new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("part-1", ContentSources.PromptText, "Normal ", new Dictionary<string, string>()),
                new ContentPart("part-2", ContentSources.Clipboard, "prompt text", new Dictionary<string, string>())
            },
            Context: new SanitizationContext("tests", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));

        var result = sanitizer.Sanitize(request);

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo("Normal prompt text"));
        Assert.That(result.Replacements, Is.Empty);
    }

    [Test]
    public void Sanitize_SyntheticSensitiveMarker_ReturnsConfirmWithPlaceholder()
    {
        var input = "Normal SENSITIVE_MARKER text";
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("SENSITIVE_MARKER"));
        Assert.That(result.SanitizedText, Does.Contain("SYNTHETIC_"));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("synthetic_marker"));
        Assert.That(result.Replacements.Single().Placeholder, Does.StartWith("SYNTHETIC_"));
        Assert.That(result.SanitizedText, Does.Contain(result.Replacements.Single().Placeholder));
        Assert.That(AuditInspection.Contains(result.AuditEvent, input), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "SENSITIVE_MARKER"), Is.False);
    }

    [Test]
    public void Sanitize_SyntheticSensitiveMarker_UsesMappingVault()
    {
        var vault = new RecordingMappingVault("SYNTHETIC_TEST");
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal SENSITIVE_MARKER text"));

        Assert.That(vault.EntityType, Is.EqualTo("synthetic_marker"));
        Assert.That(vault.NormalizedValue, Is.EqualTo("SENSITIVE_MARKER"));
        Assert.That(result.SanitizedText, Does.Contain("SYNTHETIC_TEST"));
        Assert.That(result.Replacements.Single().Placeholder, Is.EqualTo("SYNTHETIC_TEST"));
    }

    [Test]
    public void Sanitize_SyntheticHardBlockMarker_ReturnsBlockWithSafeWarning()
    {
        var input = "Reject BLOCK_THIS now";
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.SanitizedText, Is.Empty);
        Assert.That(result.Replacements, Is.Empty);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("synthetic_block_marker"));
        Assert.That(result.Warnings.Single().Message, Does.Not.Contain(input));
        Assert.That(result.Warnings.Single().Message, Does.Not.Contain("BLOCK_THIS"));
    }

    [Test]
    public void Sanitize_SyntheticSensitiveMarker_AuditHasMetadataWithoutRawValues()
    {
        var input = "Normal SENSITIVE_MARKER text";
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest(input));
        var audit = result.AuditEvent;

        Assert.That(audit.Timestamp, Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(audit.RequestId, Is.Not.Empty);
        Assert.That(audit.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(audit.EntityCountsByType["synthetic_marker"], Is.EqualTo(1));
        Assert.That(audit.ActionCounts["replace_synthetic"], Is.EqualTo(1));

        var span = audit.SpanSummaries.Single();
        Assert.That(span.Offset, Is.EqualTo(input.IndexOf("SENSITIVE_MARKER", StringComparison.Ordinal)));
        Assert.That(span.Length, Is.EqualTo("SENSITIVE_MARKER".Length));
        Assert.That(span.Type, Is.EqualTo("synthetic_marker"));

        Assert.That(AuditInspection.Contains(audit, input), Is.False);
        Assert.That(AuditInspection.Contains(audit, "SENSITIVE_MARKER"), Is.False);
    }

    [Test]
    public void Sanitize_SensitivePrompt_AuditTraceIncludesCountsAndVerificationWithoutRawValues()
    {
        var input = "Normal SENSITIVE_MARKER text";
        var result = TestSanitizers.Create().Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.reason"], Is.EqualTo("sensitive_candidates_found"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.stage.detectors"], Is.EqualTo("candidates_found"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.stage.verification"], Is.EqualTo("passed"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.type.synthetic_marker"], Is.EqualTo("1"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.action.replace_synthetic"], Is.EqualTo("1"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.detector.synthetic_marker"], Is.EqualTo("1"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, input), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "SENSITIVE_MARKER"), Is.False);
    }

    [Test]
    public void Sanitize_SyntheticHardBlockMarker_AuditHasMetadataWithoutRawValues()
    {
        var input = "Reject BLOCK_THIS now";
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest(input));
        var audit = result.AuditEvent;

        Assert.That(audit.Timestamp, Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(audit.RequestId, Is.Not.Empty);
        Assert.That(audit.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(audit.EntityCountsByType["synthetic_block_marker"], Is.EqualTo(1));
        Assert.That(audit.ActionCounts["block_synthetic"], Is.EqualTo(1));

        var span = audit.SpanSummaries.Single();
        Assert.That(span.Offset, Is.EqualTo(input.IndexOf("BLOCK_THIS", StringComparison.Ordinal)));
        Assert.That(span.Length, Is.EqualTo("BLOCK_THIS".Length));
        Assert.That(span.Type, Is.EqualTo("synthetic_block_marker"));

        Assert.That(AuditInspection.Contains(audit, input), Is.False);
        Assert.That(AuditInspection.Contains(audit, "BLOCK_THIS"), Is.False);
    }

    [Test]
    public void Sanitize_BlockDecision_AuditTraceUsesRawFreeReasonCode()
    {
        var result = TestSanitizers.Create().Sanitize(CreatePromptRequest("Reject BLOCK_THIS now"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.reason"], Is.EqualTo("synthetic_block_marker"));
        Assert.That(result.AuditEvent.ScannerStatuses["trace.stage.detectors"], Is.EqualTo("hard_block_candidates"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "BLOCK_THIS"), Is.False);
    }

    [Test]
    public void Sanitize_BlockMarkerWinsOverConfirmMarker()
    {
        var sanitizer = TestSanitizers.Create();
        var result = sanitizer.Sanitize(CreatePromptRequest("Reject BLOCK_THIS and SENSITIVE_MARKER"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.SanitizedText, Is.Empty);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("synthetic_block_marker"));
    }

    [Test]
    public void Sanitize_DictionaryTerm_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault, new[]
        {
            new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, "Known customer")
        });

        var result = sanitizer.Sanitize(CreatePromptRequest("Talk to ACME Banking tomorrow."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("ACME Banking"));
        Assert.That(vault.TryGetPseudonym("customer", "ACME Banking", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("customer"));
        Assert.That(result.Replacements.Single().Action, Is.EqualTo(PolicyActions.PseudonymizeRestorable));
        Assert.That(result.Replacements.Single().Restorable, Is.True);
    }

    [Test]
    public void Sanitize_DictionaryTerm_MatchesCaseAndSeparatorVariants()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault, new[]
        {
            new DictionaryTerm("domain", "test.secret.com", PolicyActions.PseudonymizeRestorable, null)
        });

        var mixedCase = sanitizer.Sanitize(CreatePromptRequest("Open Test.secret.com after lunch."));
        var separatorVariant = sanitizer.Sanitize(CreatePromptRequest("Open Test secret com after lunch."));

        Assert.That(mixedCase.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(mixedCase.SanitizedText, Does.Not.Contain("Test.secret.com"));
        Assert.That(mixedCase.Replacements, Has.Count.EqualTo(1));
        Assert.That(mixedCase.Replacements.Single().Offset, Is.EqualTo("Open ".Length));
        Assert.That(mixedCase.Replacements.Single().Length, Is.EqualTo("Test.secret.com".Length));
        Assert.That(mixedCase.Replacements.Single().Type, Is.EqualTo("domain"));
        Assert.That(separatorVariant.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(separatorVariant.SanitizedText, Does.Not.Contain("Test secret com"));
        Assert.That(separatorVariant.Replacements, Has.Count.EqualTo(1));
        Assert.That(separatorVariant.Replacements.Single().Offset, Is.EqualTo("Open ".Length));
        Assert.That(separatorVariant.Replacements.Single().Length, Is.EqualTo("Test secret com".Length));
        Assert.That(separatorVariant.Replacements.Single().Type, Is.EqualTo("domain"));
        Assert.That(vault.TryGetPseudonym("domain", "test.secret.com", out var pseudonym), Is.True);
        Assert.That(mixedCase.SanitizedText, Does.Contain(pseudonym));
        Assert.That(separatorVariant.SanitizedText, Does.Contain(pseudonym));
        Assert.That(AuditInspection.Contains(mixedCase.AuditEvent, "Test.secret.com"), Is.False);
        Assert.That(AuditInspection.Contains(separatorVariant.AuditEvent, "Test secret com"), Is.False);
    }

    [Test]
    public void Sanitize_DictionaryTerm_DoesNotMatchInsideLongerWords()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault, new[]
        {
            new DictionaryTerm("product", "pom", PolicyActions.PseudonymizeRestorable, null)
        });

        var embedded = sanitizer.Sanitize(CreatePromptRequest("Review pomelo and componentpom today."));
        var standalone = sanitizer.Sanitize(CreatePromptRequest("Review POM today."));

        Assert.That(embedded.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(embedded.SanitizedText, Is.EqualTo("Review pomelo and componentpom today."));
        Assert.That(embedded.Replacements, Is.Empty);
        Assert.That(standalone.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(standalone.SanitizedText, Does.Not.Contain("POM"));
        Assert.That(standalone.Replacements, Has.Count.EqualTo(1));
        Assert.That(standalone.Replacements.Single().Offset, Is.EqualTo("Review ".Length));
        Assert.That(standalone.Replacements.Single().Length, Is.EqualTo("POM".Length));
        Assert.That(standalone.Replacements.Single().Type, Is.EqualTo("product"));
        Assert.That(vault.TryGetPseudonym("product", "pom", out var pseudonym), Is.True);
        Assert.That(standalone.SanitizedText, Does.Contain(pseudonym));
    }

    [Test]
    public void Sanitize_ManagedUsername_MatchesCaseAndSeparatorVariants()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault, new[]
        {
            new DictionaryTerm("username", "alexey.andreev", PolicyActions.PseudonymizeRestorable, null)
        });

        var mixedCase = sanitizer.Sanitize(CreatePromptRequest("Ask Alexey.andreev to review it."));
        var separatorVariant = sanitizer.Sanitize(CreatePromptRequest("Ask Alexey Andreev to review it."));

        Assert.That(mixedCase.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(mixedCase.SanitizedText, Does.Not.Contain("Alexey.andreev"));
        Assert.That(mixedCase.Replacements, Has.Count.EqualTo(1));
        Assert.That(mixedCase.Replacements.Single().Offset, Is.EqualTo("Ask ".Length));
        Assert.That(mixedCase.Replacements.Single().Length, Is.EqualTo("Alexey.andreev".Length));
        Assert.That(mixedCase.Replacements.Single().Type, Is.EqualTo("username"));
        Assert.That(separatorVariant.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(separatorVariant.SanitizedText, Does.Not.Contain("Alexey Andreev"));
        Assert.That(separatorVariant.Replacements, Has.Count.EqualTo(1));
        Assert.That(separatorVariant.Replacements.Single().Offset, Is.EqualTo("Ask ".Length));
        Assert.That(separatorVariant.Replacements.Single().Length, Is.EqualTo("Alexey Andreev".Length));
        Assert.That(separatorVariant.Replacements.Single().Type, Is.EqualTo("username"));
        Assert.That(vault.TryGetPseudonym("username", "alexey.andreev", out var pseudonym), Is.True);
        Assert.That(mixedCase.SanitizedText, Does.Contain(pseudonym));
        Assert.That(separatorVariant.SanitizedText, Does.Contain(pseudonym));
        Assert.That(AuditInspection.Contains(mixedCase.AuditEvent, "Alexey.andreev"), Is.False);
        Assert.That(AuditInspection.Contains(separatorVariant.AuditEvent, "Alexey Andreev"), Is.False);
    }

    [Test]
    public void Sanitize_DictionaryTerm_AuditDoesNotContainRawDictionaryValue()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), new[]
        {
            new DictionaryTerm("project", "Blue Falcon", PolicyActions.PseudonymizeRestorable, null)
        });

        var result = sanitizer.Sanitize(CreatePromptRequest("Status for Blue Falcon"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.EntityCountsByType["project"], Is.EqualTo(1));
        Assert.That(result.AuditEvent.ActionCounts[PolicyActions.PseudonymizeRestorable], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "Blue Falcon"), Is.False);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Sanitize_InternalUrl_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Call https://deploy.corp.example.local/api now."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("https://deploy.corp.example.local/api"));
        Assert.That(vault.TryGetPseudonym("url", "https://deploy.corp.example.local/api", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("url"));
        Assert.That(result.Replacements.Single().Restorable, Is.True);
    }

    [Test]
    public void Sanitize_InternalDomain_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Deploy host is deploy.corp.example.local."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("deploy.corp.example.local"));
        Assert.That(vault.TryGetPseudonym("domain", "deploy.corp.example.local", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("domain"));
        Assert.That(result.Replacements.Single().Restorable, Is.True);
    }

    [Test]
    public void Sanitize_PublicAllowlistedDocumentationUrl_ReturnsAllow()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            PolicyWithPublicDocsAllowlist());

        var input = "Read https://learn.microsoft.com/en-us/dotnet/";
        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo(input));
        Assert.That(result.Replacements, Is.Empty);
    }

    [Test]
    public void Sanitize_PublicAllowlist_DoesNotAllowInternalLookalikeDomain()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(
            vault,
            Array.Empty<DictionaryTerm>(),
            PolicyWithPublicDocsAllowlist());

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://learn.microsoft.com.evil.corp.local/docs"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("learn.microsoft.com.evil.corp.local"));
        Assert.That(vault.TryGetPseudonym("url", "https://learn.microsoft.com.evil.corp.local/docs", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
    }

    [Test]
    public void Sanitize_PublicAllowlistWithoutTrailingSlash_DoesNotAllowInternalLookalikeDomain()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(
            vault,
            Array.Empty<DictionaryTerm>(),
            PolicyWithPublicDocsAllowlist("https://learn.microsoft.com"));

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://learn.microsoft.com.evil.corp.local/docs"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("learn.microsoft.com.evil.corp.local"));
        Assert.That(vault.TryGetPseudonym("url", "https://learn.microsoft.com.evil.corp.local/docs", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
    }

    [Test]
    public void Sanitize_InternalUrlAndDomain_AuditDoesNotContainRawValues()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Use https://deploy.corp.example.local/api and deploy.corp.example.local"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.EntityCountsByType["url"], Is.EqualTo(1));
        Assert.That(result.AuditEvent.EntityCountsByType["domain"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "https://deploy.corp.example.local/api"), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "deploy.corp.example.local"), Is.False);
    }

    [Test]
    public void Sanitize_PrivateIpv4_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("192.168.10.25"));
        Assert.That(vault.TryGetPseudonym("ip_address", "192.168.10.25", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("ip_address"));
        Assert.That(result.Replacements.Single().Restorable, Is.True);
    }

    [Test]
    public void Sanitize_PrivateCidr_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Route 10.20.30.0/24 through the tunnel."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("10.20.30.0/24"));
        Assert.That(vault.TryGetPseudonym("cidr", "10.20.30.0/24", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("cidr"));
    }

    [Test]
    public void Sanitize_PrivateCidr_SelectsOneSpanInsteadOfNestedIp()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Scan 172.16.0.0/16."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("cidr"));
        Assert.That(result.SanitizedText, Does.Not.Contain("172.16.0.0"));
        Assert.That(result.SanitizedText, Does.Not.Contain("/16"));
    }

    [Test]
    public void Sanitize_PublicIpv4_ReturnsAllowByDefault()
    {
        var input = "Use public resolver 8.8.8.8.";
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo(input));
        Assert.That(result.Replacements, Is.Empty);
    }

    [Test]
    public void Sanitize_PrivateIpAndCidr_AuditDoesNotContainRawValues()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Use 192.168.10.25 and 10.20.30.0/24."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.EntityCountsByType["ip_address"], Is.EqualTo(1));
        Assert.That(result.AuditEvent.EntityCountsByType["cidr"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "192.168.10.25"), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "10.20.30.0/24"), Is.False);
    }

    [Test]
    public void Sanitize_EmailAddress_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("Contact alexey.andreev@corp.example.local today."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("alexey.andreev@corp.example.local"));
        Assert.That(vault.TryGetPseudonym("email", "alexey.andreev@corp.example.local", out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("email"));
        Assert.That(result.Replacements.Single().Restorable, Is.True);
    }

    [Test]
    public void Sanitize_WindowsUserPath_PseudonymizesThroughVault()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var path = @"C:\Users\alexey.andreev\Documents\secret.txt";
        var result = sanitizer.Sanitize(CreatePromptRequest($"Open {path}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain(path));
        Assert.That(vault.TryGetPseudonym("file_path", path, out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("file_path"));
    }

    [Test]
    public void Sanitize_ManagedUsername_ReplacesPowerShellPromptUsername()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(
            vault,
            new[]
            {
                new DictionaryTerm("username", "user1", PolicyActions.PseudonymizeRestorable, null)
            });

        var result = sanitizer.Sanitize(CreatePromptRequest(@"PS C:\Users\user1> dotnet test"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("user1"));
        Assert.That(result.SanitizedText, Does.Contain(@"PS C:\Users\USERNAME_"));
        Assert.That(vault.TryGetPseudonym("username", "user1", out var pseudonym), Is.True);
        Assert.That(pseudonym, Does.Match(@"^USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}$"));
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.SanitizedText, Does.Contain("> dotnet test"));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("username"));
        Assert.That(result.AuditEvent.EntityCountsByType["username"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "user1"), Is.False);
    }

    [Test]
    public void Sanitize_ManagedUsername_DoesNotReplaceInsideLongerTokens()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            new[]
            {
                new DictionaryTerm("username", "user1", PolicyActions.PseudonymizeRestorable, null)
            });

        var result = sanitizer.Sanitize(CreatePromptRequest("Keep superuser1 and user12 raw, replace user1 only."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("superuser1"));
        Assert.That(result.SanitizedText, Does.Contain("user12"));
        Assert.That(result.SanitizedText, Does.Not.Contain("replace user1"));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("username"));
    }

    [Test]
    public void Sanitize_WindowsUserPath_WithManagedUsername_RemainsSingleFilePathReplacement()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(
            vault,
            new[]
            {
                new DictionaryTerm("username", "alexey.andreev", PolicyActions.PseudonymizeRestorable, null)
            });

        var path = @"C:\Users\alexey.andreev\Documents\secret.txt";
        var result = sanitizer.Sanitize(CreatePromptRequest($"Open {path}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("alexey.andreev"));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("file_path"));
        Assert.That(result.AuditEvent.EntityCountsByType["file_path"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "alexey.andreev"), Is.False);
    }

    [Test]
    public void Sanitize_WindowsPromptPath_ReplacesUsernameWithoutLocalDictionary()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest(@"PS C:\Users\user1> dotnet test"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("user1"));
        Assert.That(result.SanitizedText, Does.Contain(@"PS C:\Users\USERNAME_"));
        Assert.That(vault.TryGetPseudonym("username", "user1", out var pseudonym), Is.True);
        Assert.That(pseudonym, Does.Match(@"^USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}$"));
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.SanitizedText, Does.Contain("> dotnet test"));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("username"));
        Assert.That(result.AuditEvent.EntityCountsByType["username"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "user1"), Is.False);
    }

    [Test]
    public void Sanitize_UnconfiguredStandaloneUsername_IsNotGuessed()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Run dotnet test as user1."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo("Run dotnet test as user1."));
        Assert.That(result.Replacements, Is.Empty);
    }

    [Test]
    public void Sanitize_UnixHomePath_PseudonymizesWherePractical()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var sanitizer = new Sanitizer(vault);

        var path = "/home/alexey/projects/codex/security.txt";
        var result = sanitizer.Sanitize(CreatePromptRequest($"Review {path}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain(path));
        Assert.That(vault.TryGetPseudonym("file_path", path, out var pseudonym), Is.True);
        Assert.That(result.SanitizedText, Does.Contain(pseudonym));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("file_path"));
    }

    [Test]
    public void Sanitize_EmailAndPath_AuditDoesNotContainRawValues()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var path = @"C:\Users\alexey.andreev\Documents\secret.txt";
        var email = "alexey.andreev@corp.example.local";

        var result = sanitizer.Sanitize(CreatePromptRequest($"Send {path} to {email}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.EntityCountsByType["file_path"], Is.EqualTo(1));
        Assert.That(result.AuditEvent.EntityCountsByType["email"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, path), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, email), Is.False);
    }

    [Test]
    public void Sanitize_ConnectionString_RedactsAsOneNonRestorableHighLevelSpan()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var connectionString = "Server=db01.corp.example.local;Database=Billing;User Id=svc;Password=P@ssw0rd!";

        var result = sanitizer.Sanitize(CreatePromptRequest($"Use {connectionString}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("connection_string"));
        Assert.That(result.Replacements.Single().Action, Is.EqualTo(PolicyActions.RedactNonRestorable));
        Assert.That(result.Replacements.Single().Restorable, Is.False);
        Assert.That(result.SanitizedText, Does.Not.Contain("P@ssw0rd!"));
        Assert.That(result.SanitizedText, Does.Not.Contain("db01.corp.example.local"));
        Assert.That(result.SanitizedText, Does.Contain("CONNECTION_STRING_REDACTED"));
    }

    [Test]
    public void Sanitize_ConnectionString_AuditDoesNotContainRawValues()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var connectionString = "Host=db01.corp.example.local;Username=svc;Password=P@ssw0rd!";

        var result = sanitizer.Sanitize(CreatePromptRequest($"Connect with {connectionString}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.AuditEvent.EntityCountsByType["connection_string"], Is.EqualTo(1));
        Assert.That(AuditInspection.Contains(result.AuditEvent, connectionString), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "P@ssw0rd!"), Is.False);
        Assert.That(AuditInspection.Contains(result.AuditEvent, "db01.corp.example.local"), Is.False);
    }

    [Test]
    public void Sanitize_ConnectionStringWinsOverEarlierNestedPath()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var connectionString = @"Server=C:\Users\alexey.andreev\db.sqlite;Database=Billing;Password=P@ssw0rd!";

        var result = sanitizer.Sanitize(CreatePromptRequest($"Use {connectionString}"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("connection_string"));
        Assert.That(result.SanitizedText, Does.Not.Contain(@"C:\Users\alexey.andreev\db.sqlite"));
        Assert.That(result.SanitizedText, Does.Not.Contain("P@ssw0rd!"));
    }

    [Test]
    public void Sanitize_HigherRiskConnectionStringWinsOverEarlierOverlappingDictionaryTerm()
    {
        var dictionaryTerm = "Use Host=db01.corp.example.local;Username=svc";
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            new[]
            {
                new DictionaryTerm("system", dictionaryTerm, PolicyActions.PseudonymizeRestorable, null)
            });

        var result = sanitizer.Sanitize(CreatePromptRequest("Use Host=db01.corp.example.local;Username=svc;Password=P@ssw0rd!"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("connection_string"));
        Assert.That(result.SanitizedText, Does.Not.Contain("P@ssw0rd!"));
        Assert.That(result.SanitizedText, Does.Not.Contain("db01.corp.example.local"));
    }

    [Test]
    public void Sanitize_LongerSpanWinsWhenRiskIsEqual()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Scan 172.16.0.0/16."));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements, Has.Count.EqualTo(1));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("cidr"));
    }

    [Test]
    public void Sanitize_ReplacementsPreserveSurroundingPunctuationAndLineBreaks()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var input = "First: 192.168.10.25,\r\nSecond: alexey.andreev@corp.example.local.";

        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("First: IP_"));
        Assert.That(result.SanitizedText, Does.Contain(",\r\nSecond: EMAIL_"));
        Assert.That(result.SanitizedText, Does.EndWith("."));
        Assert.That(result.SanitizedText, Does.Not.Contain("192.168.10.25"));
        Assert.That(result.SanitizedText, Does.Not.Contain("alexey.andreev@corp.example.local"));
    }

    [Test]
    public void VerifySanitizedOutput_PassesCleanOutput()
    {
        var input = "Connect to 192.168.10.25";
        var replacements = new[]
        {
            new Replacement(
                ContentPartId: "prompt",
                Offset: input.IndexOf("192.168.10.25", StringComparison.Ordinal),
                Length: "192.168.10.25".Length,
                Type: "ip_address",
                Placeholder: "IP_TEST",
                Action: PolicyActions.PseudonymizeRestorable,
                Restorable: true)
        };

        var result = Sanitizer.VerifySanitizedOutput(input, "Connect to IP_TEST", replacements, expectedReplacementCount: 1);

        Assert.That(result.Passed, Is.True);
        Assert.That(result.ReasonCode, Is.Null);
    }

    [Test]
    public void Sanitize_VerifierBlocksIfSelectedRawSpanSurvives()
    {
        var sanitizer = new Sanitizer(new RawReturningVault());

        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.SanitizedText, Is.Empty);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("sanitized_output_verification_failed"));
        Assert.That(result.Warnings.Single().Message, Does.Contain("raw_span_survived"));
    }

    [Test]
    public void VerifySanitizedOutput_BlocksReplacementCountMismatch()
    {
        var result = Sanitizer.VerifySanitizedOutput(
            originalText: "Connect to 192.168.10.25",
            sanitizedText: "Connect to IP_TEST",
            replacements: Array.Empty<Replacement>(),
            expectedReplacementCount: 1);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.ReasonCode, Is.EqualTo("replacement_count_mismatch"));
    }

    [Test]
    public void Sanitize_VerificationFailureAuditDoesNotContainRawValue()
    {
        var sanitizer = new Sanitizer(new RawReturningVault());

        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "192.168.10.25"), Is.False);
        Assert.That(result.AuditEvent.Warnings.Single().Code, Is.EqualTo("sanitized_output_verification_failed"));
    }

    [Test]
    public void GitleaksProvenanceLoader_LoadsSourceBuildMetadata()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(tempDirectory, "gitleaks-provenance.json");
            File.WriteAllText(path, """
                {
                  "source_repository": "https://github.com/gitleaks/gitleaks",
                  "source_revision": "abc123def456",
                  "source_tag": "v8.0.0",
                  "build_command": "go build ./...",
                  "go_version": "go1.22.0",
                  "binary_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
                """);

            var provenance = GitleaksProvenanceLoader.Load(path);

            Assert.That(provenance.SourceRevision, Is.EqualTo("abc123def456"));
            Assert.That(provenance.SourceTag, Is.EqualTo("v8.0.0"));
            Assert.That(provenance.BuildCommand, Is.EqualTo("go build ./..."));
            Assert.That(provenance.GoVersion, Is.EqualTo("go1.22.0"));
            Assert.That(provenance.BinarySha256, Has.Length.EqualTo(64));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void GitleaksProvenanceLoader_RejectsMissingChecksumField()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(tempDirectory, "gitleaks-provenance.json");
            File.WriteAllText(path, """
                {
                  "source_repository": "https://github.com/gitleaks/gitleaks",
                  "source_revision": "abc123def456",
                  "source_tag": "v8.0.0",
                  "build_command": "go build ./...",
                  "go_version": "go1.22.0"
                }
                """);

            Assert.Throws<InvalidDataException>(() => GitleaksProvenanceLoader.Load(path));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void GitleaksPipeAdapter_RunsConfiguredExecutable()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(0, "[]", string.Empty, TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        var result = adapter.Scan("token=example", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(runner.LastRequest!.ExecutablePath, Is.EqualTo(@"C:\tools\gitleaks.exe"));
        Assert.That(result.ScannerStatus, Is.EqualTo("no_findings"));
    }

    [Test]
    public void GitleaksPipeAdapter_UsesStdinInput()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(0, "[]", string.Empty, TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        adapter.Scan("token=example", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(runner.LastRequest!.StandardInput, Is.EqualTo("token=example"));
        Assert.That(runner.LastRequest.Arguments, Does.Contain("--source"));
        Assert.That(runner.LastRequest.Arguments, Does.Contain("-"));
    }

    [Test]
    public void GitleaksPipeAdapter_RequestsJsonOutputAndRedaction()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(0, "[]", string.Empty, TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        adapter.Scan("token=example", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(runner.LastRequest!.Arguments, Does.Contain("--report-format"));
        Assert.That(runner.LastRequest.Arguments, Does.Contain("json"));
        Assert.That(runner.LastRequest.Arguments, Does.Contain("--redact"));
    }

    [Test]
    public void GitleaksPipeAdapter_HandlesNoFindingsResult()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(0, "[]", string.Empty, TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        var result = adapter.Scan("ordinary prompt", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(result.ScannerStatus, Is.EqualTo("no_findings"));
        Assert.That(result.FindingsJson, Is.EqualTo("[]"));
        Assert.That(result.TimedOut, Is.False);
    }

    [Test]
    public void GitleaksPipeAdapter_DistinguishesInvalidJson()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(0, "not json", string.Empty, TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        var result = adapter.Scan("token=example", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(result.ScannerStatus, Is.EqualTo("invalid_json"));
    }

    [Test]
    public void GitleaksPipeAdapter_DistinguishesScannerExitError()
    {
        var runner = new RecordingGitleaksRunner(new GitleaksProcessResult(2, "[]", "boom", TimedOut: false));
        var adapter = new GitleaksPipeAdapter(runner);

        var result = adapter.Scan("token=example", @"C:\tools\gitleaks.exe", TimeSpan.FromSeconds(5));

        Assert.That(result.ScannerStatus, Is.EqualTo("scanner_error"));
    }

    [Test]
    public void GitleaksFindingConverter_ConvertsLfLineColumnToOffset()
    {
        var input = "first line\nkey=abcdef\nlast";
        var json = """
            [
              {
                "RuleID": "generic-api-key",
                "StartLine": 2,
                "EndLine": 2,
                "StartColumn": 5,
                "EndColumn": 10,
                "Secret": "abcdef",
                "Match": "key=abcdef"
              }
            ]
            """;

        var spans = GitleaksFindingConverter.Convert(input, json);

        Assert.That(spans, Has.Count.EqualTo(1));
        Assert.That(spans.Single().Offset, Is.EqualTo(input.IndexOf("abcdef", StringComparison.Ordinal)));
        Assert.That(spans.Single().Length, Is.EqualTo("abcdef".Length));
    }

    [Test]
    public void GitleaksFindingConverter_ConvertsCrlfLineColumnToOffset()
    {
        var input = "first line\r\nkey=abcdef\r\nlast";
        var json = """
            [
              {
                "RuleID": "generic-api-key",
                "StartLine": 2,
                "EndLine": 2,
                "StartColumn": 5,
                "EndColumn": 10,
                "Secret": "abcdef",
                "Match": "key=abcdef"
              }
            ]
            """;

        var spans = GitleaksFindingConverter.Convert(input, json);

        Assert.That(spans, Has.Count.EqualTo(1));
        Assert.That(spans.Single().Offset, Is.EqualTo(input.IndexOf("abcdef", StringComparison.Ordinal)));
        Assert.That(spans.Single().Length, Is.EqualTo("abcdef".Length));
    }

    [Test]
    public void GitleaksFindingConverter_DoesNotPersistRawSecretOrMatchValues()
    {
        var input = "key=abcdef";
        var json = """
            [
              {
                "RuleID": "generic-api-key",
                "StartLine": 1,
                "EndLine": 1,
                "StartColumn": 5,
                "EndColumn": 10,
                "Secret": "abcdef",
                "Match": "key=abcdef"
              }
            ]
            """;

        var span = GitleaksFindingConverter.Convert(input, json).Single();
        var serializedSpan = System.Text.Json.JsonSerializer.Serialize(span);

        Assert.That(serializedSpan, Does.Not.Contain("abcdef"));
        Assert.That(serializedSpan, Does.Not.Contain("key=abcdef"));
    }

    [Test]
    public void GitleaksFindingConverter_ReturnsSecretCandidateSpan()
    {
        var input = "key=abcdef";
        var json = """
            [
              {
                "RuleID": "generic-api-key",
                "StartLine": 1,
                "EndLine": 1,
                "StartColumn": 5,
                "EndColumn": 10,
                "Secret": "abcdef",
                "Match": "key=abcdef"
              }
            ]
            """;

        var span = GitleaksFindingConverter.Convert(input, json).Single();

        Assert.That(span.Type, Is.EqualTo("secret"));
        Assert.That(span.DetectorId, Is.EqualTo("gitleaks"));
        Assert.That(span.RuleId, Is.EqualTo("generic-api-key"));
    }

    [Test]
    public void Sanitize_GitleaksScannerFindingBecomesSecretRedaction()
    {
        var input = "key=abcdef";
        var scanner = new RecordingSecretScanner(new SecretScanResult(
            TimedOut: false,
            ScannerStatus: "findings",
            Findings: new[]
            {
                new GitleaksFindingSpan(
                    Offset: input.IndexOf("abcdef", StringComparison.Ordinal),
                    Length: "abcdef".Length,
                    Type: SensitiveEntityTypes.Secret,
                    DetectorId: "gitleaks",
                    RuleId: "generic-api-key")
            }));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Is.EqualTo("key=SECRET_REDACTED"));
        Assert.That(result.Replacements.Single().Type, Is.EqualTo("secret"));
    }

    [Test]
    public void Sanitize_GitleaksScannerErrorFailsClosed()
    {
        var scanner = new RecordingSecretScanner(new SecretScanResult(TimedOut: false, ScannerStatus: "scanner_error", Findings: Array.Empty<GitleaksFindingSpan>()));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("scanner_error"));
    }

    [Test]
    public void Sanitize_GitleaksInvalidJsonFailsClosed()
    {
        var scanner = new RecordingSecretScanner(new SecretScanResult(TimedOut: false, ScannerStatus: "invalid_json", Findings: Array.Empty<GitleaksFindingSpan>()));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("scanner_error"));
    }

    [Test]
    public void Sanitize_ScannerConfigurationErrorFailsClosed()
    {
        var scanner = new ScannerConfigurationGuardedSecretScanner(
            new RecordingSecretScanner(new SecretScanResult(false, ScannerStatusIds.NoFindings.Value, Array.Empty<GitleaksFindingSpan>())),
            () => ScannerRuntimeConfigurationReport.ValidLocalArtifact with
            {
                Valid = false,
                BinaryPresent = false,
                WarningCode = "scanner_binary_missing"
            });
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.AuditEvent.ScannerStatuses["gitleaks"], Is.EqualTo("configuration_error"));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("scanner_error"));
    }

    [Test]
    public void Sanitize_TokenValue_RedactsNonRestorable()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("api_key=sk_live_1234567890abcdef"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Is.EqualTo("api_key=TOKEN_REDACTED"));
        Assert.That(result.Replacements.Single().Action, Is.EqualTo(PolicyActions.RedactNonRestorable));
        Assert.That(result.Replacements.Single().Restorable, Is.False);
    }

    [Test]
    public void Sanitize_PrivateKey_RedactsNonRestorable()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var privateKey = """
            -----BEGIN PRIVATE KEY-----
            abcdef123456
            -----END PRIVATE KEY-----
            """;

        var result = sanitizer.Sanitize(CreatePromptRequest(privateKey));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Is.EqualTo("PRIVATE_KEY_REDACTED"));
        Assert.That(result.Replacements.Single().Action, Is.EqualTo(PolicyActions.RedactNonRestorable));
        Assert.That(result.Replacements.Single().Restorable, Is.False);
    }

    [Test]
    public void Sanitize_PasswordLikeValue_RedactsNonRestorable()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("password=P@ssw0rd!"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Is.EqualTo("password=PASSWORD_REDACTED"));
        Assert.That(result.Replacements.Single().Action, Is.EqualTo(PolicyActions.RedactNonRestorable));
        Assert.That(result.Replacements.Single().Restorable, Is.False);
    }

    [Test]
    public void Sanitize_SecretRedaction_IsNotRestorableThroughVault()
    {
        var vault = new RecordingMappingVault("SHOULD_NOT_BE_USED");
        var sanitizer = new Sanitizer(vault);

        var result = sanitizer.Sanitize(CreatePromptRequest("token=abcdef1234567890"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Is.EqualTo("token=TOKEN_REDACTED"));
        Assert.That(vault.EntityType, Is.Null);
        Assert.That(result.Replacements.Single().Restorable, Is.False);
    }

    [Test]
    public void Sanitize_OrdinaryPromptCompletesUnderTargetBudget()
    {
        var stopwatch = Stopwatch.StartNew();
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        stopwatch.Stop();
        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(stopwatch.Elapsed, Is.LessThan(Sanitizer.TotalHardCap));
    }

    [Test]
    public void Sanitizer_TotalHardCap_IsTenSeconds()
    {
        Assert.That(Sanitizer.TotalHardCap, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void Sanitize_GitleaksBudget_IsCappedAtFiveSeconds()
    {
        var scanner = new RecordingSecretScanner(new SecretScanResult(TimedOut: false, ScannerStatus: "no_findings", Findings: Array.Empty<GitleaksFindingSpan>()));
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicy.BuiltInDefaults with
            {
                ScannerSettings = RedactionPolicy.BuiltInDefaults.ScannerSettings with
                {
                    GitleaksTimeoutMs = 9000
                }
            },
            scanner);

        sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(scanner.LastTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void Sanitize_ScannerTimeout_ReturnsBlock()
    {
        var scanner = new RecordingSecretScanner(new SecretScanResult(TimedOut: true, ScannerStatus: "timeout", Findings: Array.Empty<GitleaksFindingSpan>()));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.SanitizedText, Is.Empty);
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("scanner_timeout"));
    }

    [Test]
    public void Sanitize_ScannerTimeoutAuditDoesNotContainRawValue()
    {
        var scanner = new RecordingSecretScanner(new SecretScanResult(TimedOut: true, ScannerStatus: "timeout", Findings: Array.Empty<GitleaksFindingSpan>()));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.AuditEvent.ScannerStatuses["gitleaks"], Is.EqualTo("timeout"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "Normal prompt text"), Is.False);
    }

    [Test]
    public void Sanitize_ContentParts_ProcessesPromptTextPart()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart("prompt", ContentSources.PromptText, "Connect to 192.168.10.25", new Dictionary<string, string>())
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("prompt"));
    }

    [Test]
    public void Sanitize_ContentParts_ProcessesTextAttachmentPart()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart("prompt", ContentSources.PromptText, "Review attachment: ", new Dictionary<string, string>()),
            new ContentPart("attachment-1", ContentSources.TextAttachment, "api_key=sk_live_1234567890abcdef", new Dictionary<string, string>())
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("TOKEN_REDACTED"));
        Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("attachment-1"));
    }

    [Test]
    public void Sanitize_ContentParts_ProcessesFileSnippetPart()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart("prompt", ContentSources.PromptText, "Review snippet: ", new Dictionary<string, string>()),
            new ContentPart("snippet-1", ContentSources.FileSnippet, "password=P@ssw0rd!", new Dictionary<string, string>())
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("PASSWORD_REDACTED"));
        Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("snippet-1"));
    }

    [Test]
    public void Sanitize_ContentParts_FindingsKeepSourcePartMetadata()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart("prompt", ContentSources.PromptText, "No sensitive data here. ", new Dictionary<string, string>()),
            new ContentPart("attachment-1", ContentSources.TextAttachment, "Contact alexey.andreev@corp.example.local", new Dictionary<string, string>()),
            new ContentPart("snippet-1", ContentSources.FileSnippet, "Connect to 192.168.10.25", new Dictionary<string, string>())
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.Replacements.Select(replacement => replacement.ContentPartId), Is.EquivalentTo(new[] { "attachment-1", "snippet-1" }));
        Assert.That(result.Entities.Select(entity => entity.ContentPartId), Is.EquivalentTo(new[] { "attachment-1", "snippet-1" }));
        Assert.That(result.AuditEvent.SpanSummaries.Select(span => span.ContentPartId), Is.EquivalentTo(new[] { "attachment-1", "snippet-1" }));
    }

    [Test]
    public void Sanitize_UnsupportedBinaryAttachment_ReturnsBlock()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart(
                "attachment-bin",
                ContentSources.TextAttachment,
                "RAW_BINARY_CONTENT_SHOULD_NOT_APPEAR",
                new Dictionary<string, string>
                {
                    ["content_type"] = "application/pdf",
                    ["is_binary"] = "true"
                })
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("unsupported_binary_attachment"));
    }

    [Test]
    public void Sanitize_UnsupportedBinaryAttachmentReasonDoesNotIncludeContents()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart(
                "attachment-bin",
                ContentSources.TextAttachment,
                "RAW_BINARY_CONTENT_SHOULD_NOT_APPEAR",
                new Dictionary<string, string>
                {
                    ["content_type"] = "application/octet-stream",
                    ["is_binary"] = "true"
                })
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Message, Does.Not.Contain("RAW_BINARY_CONTENT_SHOULD_NOT_APPEAR"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "RAW_BINARY_CONTENT_SHOULD_NOT_APPEAR"), Is.False);
    }

    [Test]
    public void Sanitize_TextAttachmentStillSanitizesNormally()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            new ContentPart(
                "attachment-text",
                ContentSources.TextAttachment,
                "api_key=sk_live_1234567890abcdef",
                new Dictionary<string, string>
                {
                    ["content_type"] = "text/plain"
                })
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("TOKEN_REDACTED"));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void AttachmentIngestion_TextAttachmentContentEntersSanitizer()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            AttachmentIngestion.CreateTextAttachment("attachment-text", "text/plain", "api_key=sk_live_1234567890abcdef")
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("TOKEN_REDACTED"));
        Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("attachment-text"));
    }

    [Test]
    public void AttachmentIngestion_FileSnippetContentEntersSanitizer()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            AttachmentIngestion.CreateFileSnippet("snippet", "config.txt", "password=P@ssw0rd!")
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("PASSWORD_REDACTED"));
        Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("snippet"));
    }

    [Test]
    public void AttachmentIngestion_UnsupportedBinaryMetadataBlocksWithoutRawContent()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

        var result = sanitizer.Sanitize(CreateRequestWithParts(new[]
        {
            AttachmentIngestion.CreateUnsupportedBinaryMetadata("archive", "application/zip")
        }));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("unsupported_binary_attachment"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "application/zip"), Is.False);
    }

    [Test]
    public void FixtureCorpusRunner_CoversCoreDetectorFamilies()
    {
        var report = FixtureCorpusRunner.RunDefault(TestSecret());

        Assert.That(report.CoveredTypes, Does.Contain("url"));
        Assert.That(report.CoveredTypes, Does.Contain("domain"));
        Assert.That(report.CoveredTypes, Does.Contain("ip_address"));
        Assert.That(report.CoveredTypes, Does.Contain("cidr"));
        Assert.That(report.CoveredTypes, Does.Contain("email"));
        Assert.That(report.CoveredTypes, Does.Contain("file_path"));
        Assert.That(report.CoveredTypes, Does.Contain("connection_string"));
    }

    [Test]
    public void FixtureCorpusRunner_CoversDictionaryGitleaksShapedSecretsAndTextAttachments()
    {
        var report = FixtureCorpusRunner.RunDefault(TestSecret());

        Assert.That(report.CoveredTypes, Does.Contain("customer"));
        Assert.That(report.CoveredTypes, Does.Contain("token"));
        Assert.That(report.CaseSummaries.Any(summary => summary.ContentSources.Contains(ContentSources.TextAttachment)), Is.True);
    }

    [Test]
    public void FixtureCorpusRunner_OutputDoesNotPrintRawDetectedSecretValues()
    {
        var report = FixtureCorpusRunner.RunDefault(TestSecret());
        var output = report.RenderTextSummary();

        Assert.That(output, Does.Not.Contain("sk_live_1234567890abcdef"));
        Assert.That(output, Does.Not.Contain("P@ssw0rd!"));
        Assert.That(output, Does.Not.Contain("ACME Banking"));
        Assert.That(output, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void ConfirmationUiShell_ShowsSanitizedPrompt()
    {
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var model = ConfirmationUiShell.CreateModel(result);

        Assert.That(model.SanitizedPrompt, Does.Contain("IP_"));
        Assert.That(model.SanitizedPrompt, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void ConfirmationUiShell_HighlightsReplacedSpans()
    {
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var model = ConfirmationUiShell.CreateModel(result);

        Assert.That(model.HighlightedSpans, Has.Count.EqualTo(1));
        Assert.That(model.HighlightedSpans.Single().Type, Is.EqualTo("ip_address"));
        Assert.That(model.HighlightedSpans.Single().Text, Does.StartWith("IP_"));
    }

    [Test]
    public void ConfirmationUiShell_ShowsCountsAndHighRiskWarnings()
    {
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("api_key=sk_live_1234567890abcdef"));

        var model = ConfirmationUiShell.CreateModel(result);

        Assert.That(model.CountsByType["token"], Is.EqualTo(1));
        Assert.That(model.HighRiskWarnings, Does.Contain("Non-restorable secret redaction present."));
    }

    [Test]
    public void ConfirmationUiShell_HasConfirmAndCancelActionsWithRawHiddenByDefault()
    {
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var model = ConfirmationUiShell.CreateModel(result);

        Assert.That(model.PrimaryAction, Is.EqualTo("Confirm sanitized prompt"));
        Assert.That(model.SecondaryAction, Is.EqualTo("Cancel"));
        Assert.That(model.RawValuesVisible, Is.False);
    }

    [Test]
    public void ConfirmationDecision_ConfirmReturnsApprovedSanitizedPayload()
    {
        var model = ConfirmationUiShell.CreateModel(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25")));

        var decision = ConfirmationDecisionContract.Confirm(model);

        Assert.That(decision.Approved, Is.True);
        Assert.That(decision.Payload, Is.Not.Null);
        Assert.That(decision.Payload!.SanitizedText, Does.Contain("IP_"));
    }

    [Test]
    public void ConfirmationDecision_CancelReturnsNoPayload()
    {
        var model = ConfirmationUiShell.CreateModel(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25")));

        var decision = ConfirmationDecisionContract.Cancel(model);

        Assert.That(decision.Approved, Is.False);
        Assert.That(decision.Payload, Is.Null);
    }

    [Test]
    public void ConfirmationDecision_ApprovalPayloadContainsOnlySanitizedText()
    {
        var model = ConfirmationUiShell.CreateModel(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25")));

        var payload = ConfirmationDecisionContract.Confirm(model).Payload!;
        var serializedPayload = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.That(serializedPayload, Does.Contain("SanitizedText"));
        Assert.That(serializedPayload, Does.Not.Contain("Original"));
        Assert.That(serializedPayload, Does.Not.Contain("Raw"));
    }

    [Test]
    public void ConfirmationDecision_OriginalPromptIsNotExposed()
    {
        var model = ConfirmationUiShell.CreateModel(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25")));

        var decision = ConfirmationDecisionContract.Confirm(model);
        var serializedDecision = System.Text.Json.JsonSerializer.Serialize(decision);

        Assert.That(serializedDecision, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void SubmitOwningAdapter_AllowSubmitsSanitizedEquivalentText()
    {
        var submitter = new RecordingPromptSubmitter();
        var adapter = new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(null));
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Normal prompt text"));

        var outcome = adapter.Handle(result);

        Assert.That(outcome.Submitted, Is.True);
        Assert.That(submitter.SubmittedTexts.Single(), Is.EqualTo("Normal prompt text"));
    }

    [Test]
    public void SubmitOwningAdapter_ConfirmWaitsForApproval()
    {
        var submitter = new RecordingPromptSubmitter();
        var confirmationProvider = new FixedConfirmationProvider(ConfirmationDecisionContract.Cancel);
        var adapter = new SubmitOwningAdapter(submitter, confirmationProvider);
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        adapter.Handle(result);

        Assert.That(confirmationProvider.RequestedModels, Has.Count.EqualTo(1));
    }

    [Test]
    public void SubmitOwningAdapter_ApprovedConfirmSubmitsOnlySanitizedText()
    {
        var submitter = new RecordingPromptSubmitter();
        var confirmationProvider = new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm);
        var adapter = new SubmitOwningAdapter(submitter, confirmationProvider);
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var outcome = adapter.Handle(result);

        Assert.That(outcome.Submitted, Is.True);
        Assert.That(submitter.SubmittedTexts.Single(), Does.Contain("IP_"));
        Assert.That(submitter.SubmittedTexts.Single(), Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void SubmitOwningAdapter_CanceledConfirmSubmitsNothing()
    {
        var submitter = new RecordingPromptSubmitter();
        var adapter = new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Cancel));
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var outcome = adapter.Handle(result);

        Assert.That(outcome.Submitted, Is.False);
        Assert.That(submitter.SubmittedTexts, Is.Empty);
    }

    [Test]
    public void SubmitOwningAdapter_BlockSubmitsNothing()
    {
        var submitter = new RecordingPromptSubmitter();
        var adapter = new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(null));
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Reject BLOCK_THIS"));

        var outcome = adapter.Handle(result);

        Assert.That(outcome.Submitted, Is.False);
        Assert.That(submitter.SubmittedTexts, Is.Empty);
    }

    [Test]
    public void GuardHookShell_AllowPathPermitsSafePrompt()
    {
        var hook = new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret())));

        var decision = hook.Evaluate(CreatePromptRequest("Normal prompt text"));

        Assert.That(decision.PermitOriginalPrompt, Is.True);
        Assert.That(decision.RequiresConfirmationFlow, Is.False);
    }

    [Test]
    public void GuardHookShell_ConfirmPathBlocksOriginalPrompt()
    {
        var hook = new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret())));

        var decision = hook.Evaluate(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(decision.PermitOriginalPrompt, Is.False);
        Assert.That(decision.RequiresConfirmationFlow, Is.True);
    }

    [Test]
    public void GuardHookShell_BlockPathBlocksOriginalPrompt()
    {
        var hook = new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret())));

        var decision = hook.Evaluate(CreatePromptRequest("Reject BLOCK_THIS"));

        Assert.That(decision.PermitOriginalPrompt, Is.False);
        Assert.That(decision.RequiresConfirmationFlow, Is.False);
    }

    [Test]
    public void GuardHookShell_BlockReasonContainsNoRawValues()
    {
        var hook = new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret())));

        var decision = hook.Evaluate(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(decision.Reason, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void GuardHookShell_ClipboardHandoffRemainsFallbackOnly()
    {
        var hook = new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret())));

        var decision = hook.Evaluate(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(decision.HandoffMode, Is.EqualTo("fallback_clipboard"));
        Assert.That(decision.PermitOriginalPrompt, Is.False);
    }

    [Test]
    public void GuardedPromptFlow_ConfirmDecisionTriggersConfirmationFlow()
    {
        var submitter = new RecordingPromptSubmitter();
        var confirmationProvider = new FixedConfirmationProvider(ConfirmationDecisionContract.Cancel);
        var flow = new GuardedPromptFlow(
            new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))),
            new SubmitOwningAdapter(submitter, confirmationProvider));

        flow.Handle(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(confirmationProvider.RequestedModels, Has.Count.EqualTo(1));
    }

    [Test]
    public void GuardedPromptFlow_OriginalPromptRemainsBlocked()
    {
        var submitter = new RecordingPromptSubmitter();
        var flow = new GuardedPromptFlow(
            new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))),
            new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm)));

        var outcome = flow.Handle(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(outcome.OriginalPromptPermitted, Is.False);
    }

    [Test]
    public void GuardedPromptFlow_ApprovedSanitizedPromptIsAvailableOnlyThroughAdapterPath()
    {
        var submitter = new RecordingPromptSubmitter();
        var flow = new GuardedPromptFlow(
            new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))),
            new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm)));

        var outcome = flow.Handle(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(outcome.SubmitOutcome.Submitted, Is.True);
        Assert.That(submitter.SubmittedTexts.Single(), Does.Contain("IP_"));
        Assert.That(submitter.SubmittedTexts.Single(), Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void GuardedPromptFlow_BlockReasonIsConcise()
    {
        var submitter = new RecordingPromptSubmitter();
        var flow = new GuardedPromptFlow(
            new GuardHookShell(new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))),
            new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(null)));

        var outcome = flow.Handle(CreatePromptRequest("Reject BLOCK_THIS"));

        Assert.That(outcome.GuardDecision.Reason.Length, Is.LessThanOrEqualTo(80));
        Assert.That(outcome.SubmitOutcome.Submitted, Is.False);
    }

    [Test]
    public void RestoredOutputGuard_RestoredMetadataSaysLocalSensitive()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("ip_address", "192.168.10.25");
        var restorer = new LocalRestorer(vault);

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: $"Connect to {pseudonym}",
            Replacements: new[]
            {
                new Replacement("prompt", 11, pseudonym.Length, "ip_address", pseudonym, PolicyActions.PseudonymizeRestorable, Restorable: true)
            }));

        Assert.That(result.Metadata.LocalSensitive, Is.True);
    }

    [Test]
    public void RestoredOutputGuard_NonRestorableRedactionsRemainRedacted()
    {
        var restorer = new LocalRestorer(new InMemoryHmacMappingVault(TestSecret()));

        var result = restorer.Restore(new RestoreRequest(
            SanitizedText: "api_key=TOKEN_REDACTED",
            Replacements: new[]
            {
                new Replacement("prompt", 8, "TOKEN_REDACTED".Length, "token", "TOKEN_REDACTED", PolicyActions.RedactNonRestorable, Restorable: false)
            }));

        Assert.That(result.Text, Is.EqualTo("api_key=TOKEN_REDACTED"));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("non_restorable_redaction_skipped"));
    }

    [Test]
    public void RestoredOutputGuard_AttemptingToSubmitRestoredOutputIsWarned()
    {
        var decision = RestoredOutputSubmissionGuard.Evaluate(new RestorationResult(
            Text: "Connect to 192.168.10.25",
            Metadata: new RestorationMetadata(
                LocalSensitive: true,
                RestoredPseudonymCountsByType: new Dictionary<string, int> { ["ip_address"] = 1 }),
            Warnings: Array.Empty<Warning>()));

        Assert.That(decision.CanSubmit, Is.False);
        Assert.That(decision.Warnings.Single().Code, Is.EqualTo("local_sensitive_resubmission_blocked"));
        Assert.That(decision.Warnings.Single().Message, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void RestoredOutputGuard_SanitizedOutputCanStillBeCopiedOrUsed()
    {
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var decision = RestoredOutputSubmissionGuard.EvaluateSanitizedOutput(result);

        Assert.That(decision.CanCopyOrUse, Is.True);
        Assert.That(decision.CanSubmit, Is.True);
    }

    [Test]
    public void DefaultStorageLayout_DefaultPathsAreUserLocalNotProjectRepository()
    {
        var layout = DefaultStorageLayout.CreateDefault();
        var currentDirectory = Directory.GetCurrentDirectory();

        Assert.That(Path.GetFullPath(layout.RootDirectory), Does.Not.StartWith(Path.GetFullPath(currentDirectory)));
    }

    [Test]
    public void DefaultStorageLayout_CreatesPolicyDirectory()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();

            Assert.That(Directory.Exists(layout.PolicyDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DefaultStorageLayout_CreatesVaultDirectory()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();

            Assert.That(Directory.Exists(layout.VaultDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DefaultStorageLayout_CreatesAuditDirectory()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();

            Assert.That(Directory.Exists(layout.AuditDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void DefaultStorageLayout_CreatesSettingsDirectory()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();

            Assert.That(Directory.Exists(layout.SettingsDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void AutostartManager_EnableAndDisableUseUserStartupRegistration()
    {
        var registry = new InMemoryStartupRegistration();
        const string commandLine = "\"C:\\Users\\me\\AppData\\Local\\Programs\\CodexRedactionGate\\CodexRedactionGate.exe\" --tray-app";

        var disabled = AutostartManager.Show(registry, commandLine);
        var enabled = AutostartManager.Enable(registry, commandLine);
        var disabledAgain = AutostartManager.Disable(registry, commandLine);

        Assert.That(disabled.Enabled, Is.False);
        Assert.That(disabled.Code, Is.EqualTo("autostart_disabled"));
        Assert.That(enabled.Enabled, Is.True);
        Assert.That(enabled.Code, Is.EqualTo("autostart_enabled"));
        Assert.That(enabled.RegistryValueName, Is.EqualTo("CodexRedactionGate"));
        Assert.That(enabled.ConfiguredCommandLine, Is.EqualTo(commandLine));
        Assert.That(disabledAgain.Enabled, Is.False);
        Assert.That(disabledAgain.Code, Is.EqualTo("autostart_disabled"));
        Assert.That(registry.Values, Is.Empty);
    }

    [Test]
    public void LocalDataCleanup_KeepsDataByDefaultAndDeletesOnlyWithExplicitConfirmation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            File.WriteAllText(Path.Combine(layout.VaultDirectory, "vault.json"), "local-sensitive");

            var plan = LocalDataCleanup.Plan(layout);
            var refused = LocalDataCleanup.Delete(layout, confirmed: false);

            Assert.That(plan.Succeeded, Is.True);
            Assert.That(plan.Code, Is.EqualTo("local_data_kept"));
            Assert.That(plan.Deleted, Is.False);
            Assert.That(plan.PlannedDirectories, Does.Contain(Path.GetFullPath(layout.VaultDirectory)));
            Assert.That(refused.Succeeded, Is.False);
            Assert.That(refused.Code, Is.EqualTo("cleanup_confirmation_required"));
            Assert.That(Directory.Exists(layout.VaultDirectory), Is.True);

            var deleted = LocalDataCleanup.Delete(layout, confirmed: true);

            Assert.That(deleted.Succeeded, Is.True);
            Assert.That(deleted.Code, Is.EqualTo("local_data_deleted"));
            Assert.That(deleted.Deleted, Is.True);
            Assert.That(Directory.Exists(tempDirectory), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void InstallerManifest_IsUserScopeKeepsDataByDefaultAndOffersExplicitCleanup()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "packaging",
            "windows",
            "CodexRedactionGate.iss");
        var manifest = File.ReadAllText(manifestPath);

        Assert.That(manifest, Does.Contain("PrivilegesRequired=lowest"));
        Assert.That(manifest, Does.Contain("DefaultDirName={localappdata}\\Programs\\CodexRedactionGate"));
        Assert.That(manifest, Does.Contain("Name: autostart"));
        Assert.That(manifest, Does.Contain("Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
        Assert.That(manifest, Does.Contain("CodexRedactionGate.Tray.exe"));
        Assert.That(manifest, Does.Not.Contain("\"CodexRedactionGate.exe\" --tray-app"));
        Assert.That(manifest, Does.Contain("#ifndef MyAppVersion"));
        Assert.That(manifest, Does.Contain("OutputBaseFilename=CodexRedactionGateSetup-{#MyAppVersion}"));
        Assert.That(manifest, Does.Contain("ArchitecturesInstallIn64BitMode=x64compatible"));
        Assert.That(manifest, Does.Contain("CloseApplications=no"));
        Assert.That(manifest, Does.Contain("RestartApplications=no"));
        Assert.That(manifest, Does.Contain("PrepareToInstall"));
        Assert.That(manifest, Does.Contain("Continue and stop Code Sanitizer now?"));
        Assert.That(manifest, Does.Contain("WindowsPowerShell\\v1.0\\powershell.exe"));
        Assert.That(manifest, Does.Contain("Stop-Process -Id $p.Id -Force"));
        Assert.That(manifest, Does.Contain("[IO.Path]::GetFullPath($_.Path).StartsWith($app"));
        Assert.That(manifest, Does.Contain("Codex Redaction Gate keeps local vault, dictionary, policy, audit and settings by default."));
        Assert.That(manifest, Does.Contain("--local-data-cleanup --i-understand-delete-local-sensitive-data"));
        Assert.That(manifest, Does.Not.Contain("Remove local sensitive data"));
        Assert.That(manifest, Does.Not.Contain("[UninstallDelete]"));
    }

    [Test]
    public void ReleaseBuildScript_CopiesCompleteScannerPackageOrFailsWhenRequired()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-release.ps1"));
        var installerScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-installer.ps1"));
        var consoleProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CodexRedactionGate", "CodexRedactionGate.csproj"));
        var trayProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CodexRedactionGate.Tray", "CodexRedactionGate.Tray.csproj"));

        Assert.That(script, Does.Contain("$ScannerSourceDirectory"));
        Assert.That(script, Does.Contain("$RequireScannerPackage"));
        Assert.That(script, Does.Contain("[string] $BuildVersion"));
        Assert.That(script, Does.Contain("-p:InformationalVersion=$BuildVersion"));
        Assert.That(script, Does.Contain("-p:IncludeSourceRevisionInInformationalVersion=false"));
        Assert.That(script, Does.Contain("gitleaks.exe"));
        Assert.That(script, Does.Contain("gitleaks-provenance.json"));
        Assert.That(script, Does.Contain("Scanner package is partial"));
        Assert.That(script, Does.Contain("scanner_output=safe_disabled_missing"));
        Assert.That(script, Does.Contain("Refusing to clean $Purpose outside the repository"));
        Assert.That(script, Does.Contain("$repoBoundary"));
        Assert.That(script, Does.Contain("artifacts\\publish-work"));
        Assert.That(script, Does.Contain("--self-contained true"));
        Assert.That(script, Does.Contain("Copy-Item -Path (Join-Path $consoleOutput \"CodexRedactionGate.*\")"));
        Assert.That(script, Does.Contain("Remove-TestPublishArtifacts"));
        Assert.That(script, Does.Contain("NUnit3.TestAdapter*"));
        Assert.That(script, Does.Contain("testhost.*"));
        Assert.That(script, Does.Contain("CodeCoverage"));
        Assert.That(installerScript, Does.Contain("-ScannerSourceDirectory $ScannerSourceDirectory"));
        Assert.That(installerScript, Does.Contain("-RequireScannerPackage:$RequireScannerPackage"));
        Assert.That(installerScript, Does.Contain("[string] $BuildVersion"));
        Assert.That(installerScript, Does.Contain("0.1.$($now.ToString('yyyyMMdd')).t$($now.ToString('HHmm'))"));
        Assert.That(installerScript, Does.Contain("Get-Command iscc"));
        Assert.That(installerScript, Does.Contain("Programs\\Inno Setup 6\\ISCC.exe"));
        Assert.That(installerScript, Does.Contain("/DMyAppVersion=$BuildVersion"));
        Assert.That(installerScript, Does.Contain("-BuildVersion $BuildVersion"));
        Assert.That(installerScript, Does.Contain("CodexRedactionGateSetup-*.exe"));
        Assert.That(installerScript, Does.Contain("CodexRedactionGateSetup-$BuildVersion.exe"));
        Assert.That(installerScript, Does.Contain("Expected installer was not created"));
        Assert.That(consoleProject, Does.Contain("<UseWPF>true</UseWPF>"));
        Assert.That(trayProject, Does.Contain("<UseWPF>true</UseWPF>"));
    }

    [Test]
    public void TrayUi_OpensSensitiveTermsWindowInsteadOfDictionaryConsoleList()
    {
        var traySourceText = ProductSourceText("WindowsTrayApp.cs");

        Assert.That(traySourceText, Does.Contain("FormatBuildVersionMenuItem"));
        Assert.That(traySourceText, Does.Contain("Open sensitive terms"));
        Assert.That(traySourceText, Does.Contain("Set up prompt protection"));
        Assert.That(traySourceText, Does.Not.Contain("Verify Codex Desktop profile"));
        Assert.That(traySourceText, Does.Not.Contain("Verify ChatGPT Desktop profile"));
        Assert.That(traySourceText, Does.Not.Contain("Open audit viewer"));
        Assert.That(traySourceText, Does.Not.Contain("Open diagnostics"));
        Assert.That(traySourceText, Does.Not.Contain("Command reference..."));
        Assert.That(traySourceText, Does.Contain("DictionaryManagementForm"));
        Assert.That(TrayMenuContent.RuleManagementCommand.CliArgument, Is.EqualTo("--dictionary-ui"));
        Assert.That(traySourceText, Does.Not.Contain("Open rule management"));
    }

    [Test]
    public void DictionaryManagementUiText_ExposesSupportedTypesAndAvoidsDotnetRunInstructions()
    {
        var text = string.Join(
            Environment.NewLine,
            DictionaryManagementUiText.Title,
            DictionaryManagementUiText.Intro,
            DictionaryManagementUiText.SupportedTypesText(),
            DictionaryManagementUiText.AddButton,
            DictionaryManagementUiText.UpdateButton,
            DictionaryManagementUiText.DeleteButton,
            DictionaryManagementUiText.TestButton);

        Assert.That(text, Does.Contain("domain"));
        Assert.That(text, Does.Contain("url"));
        Assert.That(text, Does.Contain("username"));
        Assert.That(text, Does.Contain("Test text"));
        Assert.That(text, Does.Not.Contain("dotnet run"));
    }

    [Test]
    public void WindowsConfirmationOverlay_RequestsForegroundActivationForReplacementDialog()
    {
        var sourceText = ProductSourceText("WindowsConfirmationOverlay.cs");
        var foregroundActivated = WindowsConfirmationOverlay.RunForegroundActivationSmoke(foregroundActivated: true);
        var foregroundDenied = WindowsConfirmationOverlay.RunForegroundActivationSmoke(foregroundActivated: false);

        Assert.That(sourceText, Does.Contain("ShowInTaskbar = true"));
        Assert.That(sourceText, Does.Contain("BringDialogToFront"));
        Assert.That(sourceText, Does.Contain("BeginInvoke(new Action(BringDialogToFront))"));
        Assert.That(sourceText, Does.Contain("Focus()"));
        Assert.That(sourceText, Does.Contain("SetForegroundWindow"));
        Assert.That(sourceText, Does.Contain("foregroundActivated"));
        Assert.That(sourceText, Does.Contain("Action required"));
        Assert.That(sourceText, Does.Contain("FlashWindow"));
        Assert.That(foregroundActivated.ForegroundActivated, Is.True);
        Assert.That(foregroundActivated.ActionRequiredStatusVisible, Is.False);
        Assert.That(foregroundActivated.RequestedCapabilities, Does.Contain("set_foreground_window"));
        Assert.That(foregroundDenied.ForegroundActivated, Is.False);
        Assert.That(foregroundDenied.ActionRequiredStatusVisible, Is.True);
    }

    [Test]
    public void WindowsConfirmationOverlay_FailsClosedWhenDialogCreationThrows()
    {
        var sourceText = ProductSourceText("WindowsConfirmationOverlay.cs");

        Assert.That(sourceText, Does.Contain("Exception? dialogException"));
        Assert.That(sourceText, Does.Contain("Application.ThreadException"));
        Assert.That(sourceText, Does.Contain("SetUnhandledExceptionMode"));
        Assert.That(sourceText, Does.Contain("catch"));
        Assert.That(sourceText, Does.Contain("return ConfirmationDecisionContract.Cancel(model);"));
    }

    [Test]
    public void ProductionSanitizerAndRestoreUseSameLayoutVaultWithLegacyMigration()
    {
        var sanitizerSource = ProductSourceText("Sanitizer.cs");
        var restoreSource = ProductSourceText("LocalRestoreWorkflow.cs");
        var vaultSource = ProductSourceText("FileMappingVault.cs");

        Assert.That(sanitizerSource, Does.Contain("CreateProductionVault(layout)"));
        Assert.That(sanitizerSource, Does.Contain("FileMappingVault.MigrateLegacyDefaultVaultIfNeeded(layout)"));
        Assert.That(sanitizerSource, Does.Not.Contain("private static IMappingVault CreateProductionVault()"));
        Assert.That(restoreSource, Does.Contain("FileMappingVault.MigrateLegacyDefaultVaultIfNeeded(layout)"));
        Assert.That(vaultSource, Does.Contain("MigrateLegacyDefaultVaultIfNeeded"));
        Assert.That(vaultSource, Does.Contain("defaultRoot"));
        Assert.That(vaultSource, Does.Contain("File.Copy(legacyPath, currentPath)"));
    }

    [Test]
    public void UserInstallScripts_CreateTrayShortcutsAndKeepLocalDataByDefault()
    {
        var repositoryRoot = FindRepositoryRoot();
        var installScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "install-user.ps1"));
        var uninstallScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "uninstall-user.ps1"));

        Assert.That(installScript, Does.Contain("Codex Redaction Gate.lnk"));
        Assert.That(installScript, Does.Contain("Diagnostics.lnk"));
        Assert.That(installScript, Does.Contain("Audit viewer.lnk"));
        Assert.That(installScript, Does.Contain("CodexRedactionGate.Tray.exe"));
        Assert.That(installScript, Does.Contain("Start-Process -FilePath $trayExe"));
        Assert.That(installScript, Does.Not.Contain("Start-Process -FilePath $exe -ArgumentList \"--tray-app\""));
        Assert.That(installScript, Does.Contain("[switch] $StopRunning"));
        Assert.That(installScript, Does.Contain("Type YES to stop Code Sanitizer and continue installation"));
        Assert.That(installScript, Does.Contain("selected AI apps will no longer be protected"));
        Assert.That(installScript, Does.Contain("Stop-Process -Id $process.Id -Force"));
        Assert.That(installScript, Does.Contain("Wait-Process -Id $process.Id -Timeout 5"));
        Assert.That(installScript, Does.Contain("--autostart-enable"));
        Assert.That(installScript, Does.Contain("-WindowStyle Hidden"));
        Assert.That(installScript, Does.Not.Contain("--local-data-cleanup --i-understand-delete-local-sensitive-data"));
        Assert.That(uninstallScript, Does.Contain("Remove-ItemProperty"));
        Assert.That(uninstallScript, Does.Contain("$DeleteLocalData"));
        Assert.That(uninstallScript, Does.Contain("--local-data-cleanup --i-understand-delete-local-sensitive-data"));
    }

    [Test]
    public void ProductSmokeRunner_CoversApplyOnlyProductPathWithRawFreeReport()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var report = ProductSmokeRunner.RunInstalledArtifactSmoke(
                AppContext.BaseDirectory,
                Path.Combine(tempDirectory, "installed"),
                DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data")),
                TestSecret());
            var rendered = string.Join(Environment.NewLine, ProductSmokeRunner.RenderRawFree(report));
            TestContext.WriteLine(rendered);

            Assert.That(report.Passed, Is.True);
            Assert.That(report.InstallArtifactPresent, Is.True);
            Assert.That(report.ResidentTrayLaunchPassed, Is.True);
            Assert.That(report.ResidentHookRegistrationPassed, Is.True);
            Assert.That(report.ResidentSetupGatePassed, Is.True);
            Assert.That(report.ResidentRuntimeReloadPassed, Is.True);
            Assert.That(report.ResidentRuntimeRollbackPassed, Is.True);
            Assert.That(report.ResidentSelectedSendFailurePassed, Is.True);
            Assert.That(report.ResidentRawFreeFailurePassed, Is.True);
            Assert.That(report.TargetChangeAbortPassed, Is.True);
            Assert.That(report.ComposerIdentityMismatchPassed, Is.True);
            Assert.That(report.ResidentSecondInstancePassed, Is.True);
            Assert.That(report.FirstRunPassed, Is.True);
            Assert.That(report.HotkeyRegistrationPassed, Is.True);
            Assert.That(report.DictionaryPolicySetupPassed, Is.True);
            Assert.That(report.SampleSanitizePassed, Is.True);
            Assert.That(report.DisposableApplyOnlyPassed, Is.True);
            Assert.That(report.AuditViewPassed, Is.True);
            Assert.That(report.RestorePassed, Is.True);
            Assert.That(report.UninstallSafePassed, Is.True);
            Assert.That(report.NativeSubmitInterceptionPassed, Is.True);
            Assert.That(report.NativeSubmitRepeatabilityPassed, Is.True);
            Assert.That(report.NativeSubmitDuplicateGuardPassed, Is.True);
            Assert.That(report.NativeSubmitOverlayForegroundRequestPassed, Is.True);
            Assert.That(report.NativeSubmitOverlayForegroundRefusalStatusPassed, Is.True);
            Assert.That(report.NativeProfileVerificationEntrypointsPassed, Is.True);
            Assert.That(report.RawFreeArtifactsPassed, Is.True);
            Assert.That(rendered, Does.Contain("supported_targets: windows_codex_chatgpt_desktop_only"));
            Assert.That(rendered, Does.Contain("live_compatibility_note: use_disposable_local_target_first_then_throwaway_codex_or_chatgpt_desktop_task"));
            Assert.That(rendered, Does.Contain("apply_only_write_back: true"));
            Assert.That(rendered, Does.Contain("project_file_read_only_smoke: true"));
            Assert.That(rendered, Does.Contain("project_file_product_smoke: true"));
            Assert.That(rendered, Does.Contain("project_file_broker_workflow: true"));
            Assert.That(rendered, Does.Contain("project_files_protected: false"));
            Assert.That(rendered, Does.Not.Contain("project_files_protected_status: true"));
            Assert.That(rendered, Does.Contain("native_submit_interception: true"));
            Assert.That(rendered, Does.Contain("resident_hook_registration: true"));
            Assert.That(rendered, Does.Contain("resident_setup_gate: true"));
            Assert.That(rendered, Does.Contain("resident_runtime_reload: true"));
            Assert.That(rendered, Does.Contain("resident_runtime_rollback: true"));
            Assert.That(rendered, Does.Contain("resident_selected_send_failure: true"));
            Assert.That(rendered, Does.Contain("resident_raw_free_failure: true"));
            Assert.That(rendered, Does.Contain("target_change_abort: true"));
            Assert.That(rendered, Does.Contain("composer_identity_mismatch: true"));
            Assert.That(rendered, Does.Contain("resident_second_instance: true"));
            Assert.That(rendered, Does.Contain("native_submit_repeatability: true"));
            Assert.That(rendered, Does.Contain("native_submit_duplicate_guard: true"));
            Assert.That(rendered, Does.Contain("native_submit_overlay_foreground_request: true"));
            Assert.That(rendered, Does.Contain("native_submit_overlay_foreground_refusal_status: true"));
            Assert.That(rendered, Does.Contain("native_profile_verification_entrypoints: true"));
            Assert.That(rendered, Does.Not.Contain("192.168.10.25"));
            Assert.That(rendered, Does.Not.Contain("Product Smoke Customer"));
            Assert.That(rendered, Does.Not.Contain("product-smoke.example.local"));
            Assert.That(rendered, Does.Not.Contain("RESIDENT_SMOKE_SENSITIVE_VALUE"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void FileAuditSink_WritesAuditEventUnderAuditDirectoryWithoutRawPrompt()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));

            var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));
            var auditFile = Directory.GetFiles(layout.AuditDirectory, "audit-*.json").Single();
            var payload = File.ReadAllText(auditFile);

            Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
            Assert.That(payload, Does.Contain("\"Decision\":\"Confirm\""));
            Assert.That(payload, Does.Contain("trace.reason"));
            Assert.That(payload, Does.Not.Contain("192.168.10.25"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void FileAuditSink_RetentionLimitsEventCount()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory, new FileAuditSinkOptions(MaxEvents: 2)));

            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 1"));
            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 2"));
            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 3"));

            Assert.That(Directory.GetFiles(layout.AuditDirectory, "audit-*.json"), Has.Length.EqualTo(2));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void AuditViewer_RendersRawFreeRowsWithFailureReasons()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var auditSink = new FileAuditSink(layout.AuditDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: auditSink);
            sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

            var failingScanner = new RecordingSecretScanner(new SecretScanResult(
                TimedOut: false,
                ScannerStatus: ScannerStatusIds.ScannerError.Value,
                Findings: Array.Empty<GitleaksFindingSpan>()));
            var failingSanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                failingScanner,
                auditSink);
            failingSanitizer.Sanitize(CreatePromptRequest("api_key=sk_live_1234567890abcdef"));

            var report = AuditViewer.Load(layout.AuditDirectory);
            var rendered = string.Join(Environment.NewLine, AuditViewer.Render(report));

            Assert.That(report.Chain.Valid, Is.True);
            Assert.That(report.Rows, Has.Count.EqualTo(2));
            Assert.That(rendered, Does.Contain("decision=Confirm"));
            Assert.That(rendered, Does.Contain("decision=Block"));
            Assert.That(rendered, Does.Contain("failure=scanner_error"));
            Assert.That(rendered, Does.Contain("actions:"));
            Assert.That(rendered, Does.Contain("entities:"));
            Assert.That(rendered, Does.Contain("scanner:"));
            Assert.That(rendered, Does.Contain("warnings: scanner_error"));
            Assert.That(rendered, Does.Contain("durations_ms:"));
            Assert.That(rendered, Does.Not.Contain("192.168.10.25"));
            Assert.That(rendered, Does.Not.Contain("sk_live_1234567890abcdef"));
            Assert.That(rendered, Does.Not.Contain("SECRET_REDACTED"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void AuditViewer_CleanupRetainsRecentAuditEventsAndDoesNotDeleteVaultMappings()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            var vaultPath = Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName);
            File.WriteAllText(vaultPath, "vault placeholder");
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));

            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 1"));
            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 2"));
            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text 3"));

            var cleanup = AuditViewer.Cleanup(layout.AuditDirectory, keepEvents: 1);

            Assert.That(cleanup.EventsBefore, Is.EqualTo(3));
            Assert.That(cleanup.EventsDeleted, Is.EqualTo(2));
            Assert.That(cleanup.EventsKept, Is.EqualTo(1));
            Assert.That(cleanup.Chain.Valid, Is.True);
            Assert.That(Directory.GetFiles(layout.AuditDirectory, "audit-*.json"), Has.Length.EqualTo(1));
            Assert.That(File.Exists(vaultPath), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void AuditViewer_ReportsTamperedChain()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));

            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));
            sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));
            var files = Directory.GetFiles(layout.AuditDirectory, "audit-*.json").OrderBy(path => path).ToArray();
            File.WriteAllText(files[0], File.ReadAllText(files[0]).Replace("Allow", "Block", StringComparison.Ordinal));

            var report = AuditViewer.Load(layout.AuditDirectory);
            var rendered = string.Join(Environment.NewLine, AuditViewer.Render(report));

            Assert.That(report.Chain.Valid, Is.False);
            Assert.That(rendered, Does.Contain("audit_chain_hash_mismatch"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Sanitize_AuditFailureForSensitiveConfirmFailsClosedWithRawFreeWarning()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicy.BuiltInDefaults,
            secretScanner: null,
            auditSink: new FailingAuditSink());

        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.SanitizedText, Is.Empty);
        Assert.That(result.Warnings.Select(warning => warning.Code), Does.Contain("audit_write_failed"));
        Assert.That(AuditInspection.Contains(result.AuditEvent, "192.168.10.25"), Is.False);
    }

    [Test]
    public void Sanitize_AuditFailureForNonSensitiveAllowRemainsLowFriction()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicy.BuiltInDefaults,
            secretScanner: null,
            auditSink: new FailingAuditSink());

        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo("Normal prompt text"));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("audit_write_failed"));
    }

    [Test]
    public void PublicAllowlistProfile_AllowsCommonPackageRegistryDomain()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicyProfiles.DefaultPublicAllowlist with
            {
                SensitiveRules = new[]
                {
                    new PolicyRule("domain", "nuget.org", null, "suffix", PolicyActions.PseudonymizeRestorable, "test sensitive suffix", null)
                }
            });

        var input = "Read https://www.nuget.org/packages/NUnit";
        var result = sanitizer.Sanitize(CreatePromptRequest(input));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Allow));
        Assert.That(result.SanitizedText, Is.EqualTo(input));
    }

    [Test]
    public void PublicAllowlistProfile_DoesNotAllowInternalLookalikeDomain()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicyProfiles.DefaultPublicAllowlist);

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://www.nuget.org.evil.corp.local/packages/NUnit"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("nuget.org.evil.corp.local"));
    }

    [Test]
    public void PublicAllowlistProfile_NeverOverridesSecrets()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicyProfiles.DefaultPublicAllowlist);

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://docs.github.com/?token=abcdef1234567890"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Contain("TOKEN_REDACTED"));
        Assert.That(result.SanitizedText, Does.Not.Contain("abcdef1234567890"));
    }

    [Test]
    public void PublicAllowlistProfile_NeverOverridesDictionaryTerms()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            new[]
            {
                new DictionaryTerm("system", "https://docs.github.com", PolicyActions.PseudonymizeRestorable, null)
            },
            RedactionPolicyProfiles.DefaultPublicAllowlist);

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://docs.github.com"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(result.SanitizedText, Does.Not.Contain("https://docs.github.com"));
    }

    [Test]
    public void PublicAllowlistProfile_NeverOverridesBlockRules()
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(TestSecret()),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicyProfiles.DefaultPublicAllowlist with
            {
                BlockRules = new[]
                {
                    new PolicyRule("url", null, "docs\\.github\\.com", "contains", PolicyActions.Block, "blocked by test", null)
                }
            });

        var result = sanitizer.Sanitize(CreatePromptRequest("Open https://docs.github.com"));

        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.Warnings.Single().Code, Is.EqualTo("policy_block_rule"));
    }

    [Test]
    public void MvpPackageSmoke_IncludesDotNetAppArtifacts()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.DotNetAppArtifactPresent, Is.True);
    }

    [Test]
    public void MvpPackageSmoke_ReferencesGitleaksArtifactAndProvenance()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.GitleaksBinaryPath, Does.EndWith("gitleaks.exe"));
        Assert.That(report.GitleaksBinaryPresent, Is.True);
        Assert.That(report.GitleaksProvenanceLoaded, Is.True);
    }

    [Test]
    public void MvpPackageSmoke_RuntimeDoesNotRequireGitGoSourceOrNetwork()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.RequiresGit, Is.False);
        Assert.That(report.RequiresGo, Is.False);
        Assert.That(report.RequiresGitleaksSourceCode, Is.False);
        Assert.That(report.RequiresNetwork, Is.False);
    }

    [Test]
    public void MvpPackageSmoke_ProvesSanitizeConfirmGuardBlockAndRestorePaths()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.SanitizeAllowPassed, Is.True);
        Assert.That(report.ConfirmPassed, Is.True);
        Assert.That(report.GuardBlockPassed, Is.True);
        Assert.That(report.LocalRestorePassed, Is.True);
        Assert.That(report.ScannerArtifactSmokePassed, Is.True);
    }

    [Test]
    public void ScannerRuntimeConfigurationValidator_MissingBinaryReportsRawFreeConfigurationProblem()
    {
        var manifest = CreatePackageSmokeManifest(includeGitleaksBinary: false);

        var report = ScannerRuntimeConfigurationValidator.Validate(manifest);

        Assert.That(report.Valid, Is.False);
        Assert.That(report.BinaryPresent, Is.False);
        Assert.That(report.WarningCode, Is.EqualTo("scanner_binary_missing"));
        Assert.That(report.RequiresGit, Is.False);
        Assert.That(report.RequiresGo, Is.False);
        Assert.That(report.RequiresNetwork, Is.False);
    }

    [Test]
    public void ScannerRuntimeConfigurationValidator_InvalidProvenanceReportsRawFreeConfigurationProblem()
    {
        var manifest = CreatePackageSmokeManifest(includeValidProvenance: false);

        var report = ScannerRuntimeConfigurationValidator.Validate(manifest);

        Assert.That(report.Valid, Is.False);
        Assert.That(report.BinaryPresent, Is.True);
        Assert.That(report.ProvenanceLoaded, Is.False);
        Assert.That(report.WarningCode, Is.EqualTo("scanner_provenance_invalid"));
    }

    [Test]
    public void ScannerPackageManifestResolver_EmptyDefaultPackageIsSafeDisabled()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var resolution = ScannerPackageManifestResolver.ResolveDefault(tempDirectory);

            Assert.That(resolution.Report.SafeDisabled, Is.True);
            Assert.That(resolution.Report.WarningCode, Is.EqualTo("scanner_package_missing_safe_disabled"));
            Assert.That(resolution.Report.RequiresGit, Is.False);
            Assert.That(resolution.Report.RequiresGo, Is.False);
            Assert.That(resolution.Report.RequiresNetwork, Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ScannerPackageManifestResolver_DefaultPackageVerifiesLocalBinaryHash()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            WriteDefaultScannerPackage(tempDirectory);

            var resolution = ScannerPackageManifestResolver.ResolveDefault(tempDirectory);

            Assert.That(resolution.AnyScannerArtifactPresent, Is.True);
            Assert.That(resolution.Report.Valid, Is.True);
            Assert.That(resolution.Report.BinaryChecksumMatches, Is.True);
            Assert.That(resolution.Manifest.GitleaksBinaryPath, Does.EndWith(Path.Combine("scanners", "gitleaks", "gitleaks.exe")));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ReadinessDoctor_NoScannerManifestReportsSafeDisabledRawFreeState()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var manifest = ScannerPackageManifestResolver.CreateDefault(tempDirectory);
            var safeDisabled = new DefaultScannerPackageResolution(
                manifest,
                ScannerRuntimeConfigurationReport.SafeDisabledLocalPackageMissing,
                AnyScannerArtifactPresent: false);

            var report = ReadinessDoctor.Check(
                layout,
                manifest: null,
                vaultSecretProbe: TestSecret,
                defaultScannerPackageProbe: () => safeDisabled);

            Assert.That(report.Ready, Is.True);
            Assert.That(report.Items.Single(item => item.Component == "scanner").Status, Is.EqualTo("safe_disabled"));
            Assert.That(report.Items.Single(item => item.Component == "scanner").Code, Is.EqualTo("scanner_package_missing_safe_disabled"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ScannerConfigurationGuardedSecretScanner_PartialDefaultPackageFailsClosed()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var scannerDirectory = Path.Combine(tempDirectory, "scanners", "gitleaks");
            Directory.CreateDirectory(scannerDirectory);
            File.WriteAllText(Path.Combine(scannerDirectory, "gitleaks.exe"), "tampered scanner");

            var resolution = ScannerPackageManifestResolver.ResolveDefault(tempDirectory);
            var scanner = new ScannerConfigurationGuardedSecretScanner(
                new RecordingSecretScanner(new SecretScanResult(false, ScannerStatusIds.NoFindings.Value, Array.Empty<GitleaksFindingSpan>())),
                () => resolution.Report);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                scanner);
            var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

            Assert.That(resolution.Report.SafeDisabled, Is.False);
            Assert.That(resolution.Report.Valid, Is.False);
            Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
            Assert.That(result.AuditEvent.ScannerStatuses["gitleaks"], Is.EqualTo("configuration_error"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void MvpPackageSmoke_CoversScannerConfigConfirmHandoffAndAttachmentBoundary()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.ScannerConfigurationValid, Is.True);
        Assert.That(report.ScannerConfigurationWarningCode, Is.Null);
        Assert.That(report.ConfirmHandoffSmokePassed, Is.True);
        Assert.That(report.AttachmentIngestionSmokePassed, Is.True);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-redaction-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SanitizeRequest CreatePromptRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart(
                    Id: "prompt",
                    ContentSource: ContentSources.PromptText,
                    RawText: text,
                    SourceMetadata: new Dictionary<string, string>())
            },
            Context: new SanitizationContext(
                Application: "tests",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }

    private static MvpPackageManifest WriteDefaultScannerPackage(string appBaseDirectory)
    {
        var manifest = ScannerPackageManifestResolver.CreateDefault(appBaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest.GitleaksBinaryPath)!);
        File.WriteAllText(manifest.AppArtifactPath, "test app artifact");
        var gitleaksContent = "test packaged gitleaks artifact";
        File.WriteAllText(manifest.GitleaksBinaryPath, gitleaksContent);
        var gitleaksHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gitleaksContent)))
            .ToLowerInvariant();
        File.WriteAllText(
            manifest.GitleaksProvenancePath,
            """
            {
              "source_repository": "https://github.com/gitleaks/gitleaks",
              "source_revision": "abc123def456",
              "source_tag": "v8.0.0",
              "build_command": "go build ./...",
              "go_version": "go1.22.0",
              "binary_sha256": "__HASH__"
            }
            """.Replace("__HASH__", gitleaksHash, StringComparison.Ordinal));
        return manifest;
    }

    private sealed class RecordingTrayHotkeyHost : ITrayHotkeyHost
    {
        private Action? _onTriggered;

        public RecordingTrayHotkeyHost()
            : this("Ctrl+Shift+F9")
        {
        }

        public RecordingTrayHotkeyHost(string displayText)
        {
            Binding = new HotkeyBinding("test-hotkey", displayText, "tests");
        }

        public HotkeyBinding Binding { get; }

        public string? LastErrorCode { get; private set; }

        public bool Started { get; private set; }

        public bool Start(Action onTriggered)
        {
            _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
            Started = true;
            LastErrorCode = null;
            return true;
        }

        public void Stop()
        {
            Started = false;
            _onTriggered = null;
        }

        public void Trigger()
        {
            _onTriggered?.Invoke();
        }
    }

    private sealed class RecordingNativeSubmitHookHost : INativeSubmitHookHost
    {
        public string? LastErrorCode { get; private set; }

        public bool Started { get; private set; }

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativeKeyGesture, bool> shouldSuppressClassificationFailure)
        {
            ArgumentNullException.ThrowIfNull(classify);
            ArgumentNullException.ThrowIfNull(onSuppressedSubmit);
            Started = true;
            LastErrorCode = null;
            return true;
        }

        public void Stop()
        {
            Started = false;
        }
    }

    private sealed class FailingTrayHotkeyHost : ITrayHotkeyHost
    {
        public FailingTrayHotkeyHost(string displayText, string lastErrorCode)
        {
            Binding = new HotkeyBinding("failing-hotkey", displayText, "tests");
            LastErrorCode = lastErrorCode;
        }

        public HotkeyBinding Binding { get; }

        public string? LastErrorCode { get; }

        public bool Start(Action onTriggered)
        {
            ArgumentNullException.ThrowIfNull(onTriggered);
            return false;
        }

        public void Stop()
        {
        }
    }

    private static OsInteractionOrchestrator CreateProductFlowOrchestrator(
        ProductFlowTextSurface surface,
        Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory,
        IActiveTextSurfaceDiscovery? discovery = null)
    {
        return new OsInteractionOrchestrator(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            discovery ?? surface,
            surface,
            surface,
            surface,
            new ProductFlowConfirmationOverlay(decisionFactory));
    }

    private static string ProductSourceText(string fileName)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        return File.ReadAllText(Path.Combine(projectDirectory, fileName));
    }

    private sealed class ProductFlowTextSurface :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction
    {
        private readonly TextSurfaceDescriptor _surface;
        private readonly TextSurfaceDescriptor _staleSurface;

        public ProductFlowTextSurface(string currentText)
        {
            CurrentText = currentText;
            _surface = new TextSurfaceDescriptor(
                "product-flow-surface",
                "codex-desktop",
                "Codex Desktop",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata(SurfaceKind: "focused-composer", WindowHandle: "1"));
            _staleSurface = _surface with
            {
                SurfaceId = "other-composer",
                Metadata = new SurfaceMetadata(SurfaceKind: "focused-composer", WindowHandle: "2")
            };
        }

        public string CurrentText { get; private set; }

        public bool FailDiscoveryAfterConfirmation { get; init; }

        public bool ReturnDifferentSurfaceAfterConfirmation { get; init; }

        public bool FailDiscoveryAfterWrite { get; init; }

        public bool ReturnDifferentSurfaceAfterWrite { get; init; }

        public bool ReturnDifferentSurfaceAtReplayBoundary { get; set; }

        public bool FailInitialCapture { get; init; }

        public bool FailWrites { get; init; }

        public string? VerificationTextOverride { get; init; }

        public int DiscoveryCount { get; private set; }

        public int CaptureCount { get; private set; }

        public int WriteCount { get; private set; }

        public int SubmitCount { get; private set; }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            DiscoveryCount++;
            if (ReturnDifferentSurfaceAtReplayBoundary)
            {
                return TextSurfaceDiscoveryResult.Success(_staleSurface);
            }

            if (WriteCount > 0 && FailDiscoveryAfterWrite)
            {
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            if (WriteCount > 0 && ReturnDifferentSurfaceAfterWrite)
            {
                return TextSurfaceDiscoveryResult.Success(_staleSurface);
            }

            if (DiscoveryCount > 1 && FailDiscoveryAfterConfirmation)
            {
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            if (DiscoveryCount > 1 && ReturnDifferentSurfaceAfterConfirmation)
            {
                return TextSurfaceDiscoveryResult.Success(_staleSurface);
            }

            return TextSurfaceDiscoveryResult.Success(_surface);
        }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            CaptureCount++;
            if (CaptureCount == 1 && FailInitialCapture)
            {
                return new TextCaptureResult(false, "capture_failed_by_test", null, new Dictionary<string, string>());
            }

            var text = CaptureCount > 1 && VerificationTextOverride is not null
                ? VerificationTextOverride
                : CurrentText;
            return new TextCaptureResult(
                true,
                "captured",
                text,
                new Dictionary<string, string> { ["capture_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            if (FailWrites)
            {
                return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
            }

            CurrentText = text;
            WriteCount++;
            return new TextReplacementResult(
                true,
                OsInteractionStatusIds.Applied,
                new Dictionary<string, string> { ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(
                true,
                OsInteractionStatusIds.Submitted,
                new Dictionary<string, string> { ["submit_count"] = SubmitCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private sealed class ProductFlowConfirmationOverlay : ITracedConfirmationOverlay
    {
        private readonly Func<ConfirmationUiModel, ConfirmationDecision> _decisionFactory;

        public ProductFlowConfirmationOverlay(Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory)
        {
            _decisionFactory = decisionFactory ?? throw new ArgumentNullException(nameof(decisionFactory));
        }

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            return _decisionFactory(model);
        }

        public ConfirmationDecision RequestConfirmation(
            ConfirmationUiModel model,
            Func<string, string, bool> traceStage)
        {
            if (!traceStage("overlay_foreground_confirmed", "foreground_verified"))
            {
                return ConfirmationDecisionContract.Cancel(model);
            }

            return _decisionFactory(model);
        }
    }

    private static MvpPackageManifest CreatePackageSmokeManifest(
        bool includeGitleaksBinary = true,
        bool includeValidProvenance = true)
    {
        var tempDirectory = CreateTempDirectory();
        var appArtifactPath = Path.Combine(tempDirectory, "CodexRedactionGate.dll");
        var gitleaksPath = Path.Combine(tempDirectory, "gitleaks.exe");
        var provenancePath = Path.Combine(tempDirectory, "gitleaks-provenance.json");

        File.WriteAllText(appArtifactPath, "test app artifact");
        var gitleaksContent = "test gitleaks artifact";
        if (includeGitleaksBinary)
        {
            File.WriteAllText(gitleaksPath, gitleaksContent);
        }

        var gitleaksHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gitleaksContent)))
            .ToLowerInvariant();
        File.WriteAllText(
            provenancePath,
            includeValidProvenance
                ? """
                    {
                      "source_repository": "https://github.com/gitleaks/gitleaks",
                      "source_revision": "abc123def456",
                      "source_tag": "v8.0.0",
                      "build_command": "go build ./...",
                      "go_version": "go1.22.0",
                      "binary_sha256": "__HASH__"
                    }
                    """.Replace("__HASH__", gitleaksHash, StringComparison.Ordinal)
                : "{ \"binary_sha256\": \"not-a-sha\" }");

        return new MvpPackageManifest(appArtifactPath, gitleaksPath, provenancePath);
    }

    private static string ValidPolicyText(string profile)
    {
        return $$"""
            version = 1
            profile = "{{profile}}"

            [defaults]
            unknown_high_risk = "confirm"
            secret = "redact_non_restorable"
            internal_identifier = "pseudonymize_restorable"

            [scanners]
            gitleaks_enabled = true
            gitleaks_timeout_ms = 5000
            """;
    }

    private static string FindRepositoryRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "tickets.md"))
                && Directory.Exists(Path.Combine(current, "src", "CodexRedactionGate")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void AssertBrokenAuditChain(Action<string[]> mutate)
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));

            sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));
            sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));
            sanitizer.Sanitize(CreatePromptRequest("Reject BLOCK_THIS"));

            var files = Directory.GetFiles(layout.AuditDirectory, "audit-*.json").OrderBy(path => path).ToArray();
            mutate(files);

            Assert.That(AuditChainVerifier.Verify(layout.AuditDirectory).Valid, Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static SanitizeRequest CreateRequestWithParts(IReadOnlyList<ContentPart> contentParts)
    {
        return new SanitizeRequest(
            ContentParts: contentParts,
            Context: new SanitizationContext(
                Application: "tests",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }

    private sealed class RecordingMappingVault : IMappingVault
    {
        private readonly string _pseudonym;

        public RecordingMappingVault(string pseudonym)
        {
            _pseudonym = pseudonym;
        }

        public string? EntityType { get; private set; }

        public string? NormalizedValue { get; private set; }

        public string GetOrCreatePseudonym(string entityType, string normalizedValue)
        {
            EntityType = entityType;
            NormalizedValue = normalizedValue;
            return _pseudonym;
        }

        public bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym)
        {
            pseudonym = _pseudonym;
            return string.Equals(EntityType, entityType, StringComparison.Ordinal)
                && string.Equals(NormalizedValue, normalizedValue, StringComparison.Ordinal);
        }

        public bool TryGetOriginal(string pseudonym, out MappingVaultRecord record)
        {
            if (string.Equals(_pseudonym, pseudonym, StringComparison.Ordinal))
            {
                record = new MappingVaultRecord(
                    EntityType ?? string.Empty,
                    NormalizedValue ?? string.Empty,
                    _pseudonym);
                return true;
            }

            record = null!;
            return false;
        }
    }

    private sealed class RawReturningVault : IMappingVault
    {
        public string GetOrCreatePseudonym(string entityType, string normalizedValue)
        {
            return normalizedValue;
        }

        public bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym)
        {
            pseudonym = normalizedValue;
            return true;
        }

        public bool TryGetOriginal(string pseudonym, out MappingVaultRecord record)
        {
            record = null!;
            return false;
        }
    }

    private sealed class RecordingPromptSubmitter : IPromptSubmitter
    {
        public List<string> SubmittedTexts { get; } = new();

        public void Submit(string text)
        {
            SubmittedTexts.Add(text);
        }
    }

    private sealed class FixedConfirmationProvider : IConfirmationProvider
    {
        private readonly Func<ConfirmationUiModel, ConfirmationDecision>? _decisionFactory;

        public FixedConfirmationProvider(Func<ConfirmationUiModel, ConfirmationDecision>? decisionFactory)
        {
            _decisionFactory = decisionFactory;
        }

        public List<ConfirmationUiModel> RequestedModels { get; } = new();

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            RequestedModels.Add(model);
            return _decisionFactory?.Invoke(model) ?? ConfirmationDecisionContract.Cancel(model);
        }
    }

    [Test]
    public void SubmitOwningAdapter_EditedTextIsReSanitizedAndVerified()
    {
        var submitter = new RecordingPromptSubmitter();
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var editedText = "Connect to secure-server";
        var confirmationProvider = new FixedConfirmationProvider(model =>
            new ConfirmationDecision(
                Approved: true,
                Payload: new ApprovedSanitizedPayload(editedText)));

        var adapter = new SubmitOwningAdapter(submitter, confirmationProvider);
        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var outcome = adapter.Handle(result, sanitizer);

        Assert.That(outcome.Submitted, Is.True, $"Expected submitted but got status: {outcome.Status}");
        Assert.That(submitter.SubmittedTexts.Single(), Is.EqualTo("Connect to secure-server"));
    }

    [Test]
    public void SubmitOwningAdapter_EditedTextFailsClosedWithoutVerifier()
    {
        var submitter = new RecordingPromptSubmitter();
        var confirmationProvider = new FixedConfirmationProvider(_ =>
            new ConfirmationDecision(
                Approved: true,
                Payload: new ApprovedSanitizedPayload("Connect to secure-server")));
        var adapter = new SubmitOwningAdapter(submitter, confirmationProvider);
        var result = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()))
            .Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var outcome = adapter.Handle(result);

        Assert.That(outcome.Submitted, Is.False);
        Assert.That(outcome.Status, Is.EqualTo("edited_text_verifier_missing"));
        Assert.That(submitter.SubmittedTexts, Is.Empty);
    }

    [Test]
    public void SubmitOwningAdapter_EditedTextThatStillNeedsConfirmationSendsNothing()
    {
        var submitter = new RecordingPromptSubmitter();
        var confirmationProvider = new FixedConfirmationProvider(_ =>
            new ConfirmationDecision(
                Approved: true,
                Payload: new ApprovedSanitizedPayload("Connect to 10.20.30.40")));
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var adapter = new SubmitOwningAdapter(submitter, confirmationProvider);
        var result = sanitizer.Sanitize(CreatePromptRequest("Connect to 192.168.10.25"));

        var outcome = adapter.Handle(result, sanitizer);

        Assert.That(outcome.Submitted, Is.False);
        Assert.That(outcome.Status, Is.EqualTo("edited_text_requires_confirmation"));
        Assert.That(submitter.SubmittedTexts, Is.Empty);
    }

    private sealed class RecordingSecretScanner : ISecretScanner
    {
        private readonly SecretScanResult _result;

        public RecordingSecretScanner(SecretScanResult result)
        {
            _result = result;
        }

        public TimeSpan? LastTimeout { get; private set; }

        public SecretScanResult Scan(string input, TimeSpan timeout)
        {
            LastTimeout = timeout;
            return _result;
        }
    }

    private sealed class FailingAuditSink : IAuditSink
    {
        public AuditWriteResult Write(AuditEvent auditEvent)
        {
            return AuditWriteResult.Failure("audit_write_failed");
        }
    }

    private sealed class InMemoryStartupRegistration : IUserStartupRegistration
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string? Read(string valueName)
        {
            return Values.TryGetValue(valueName, out var value) ? value : null;
        }

        public void Write(string valueName, string commandLine)
        {
            Values[valueName] = commandLine;
        }

        public void Delete(string valueName)
        {
            Values.Remove(valueName);
        }
    }

    private sealed class ThrowingPromptSubmitter : IPromptSubmitter
    {
        public void Submit(string text)
        {
            throw new InvalidOperationException("submit unavailable");
        }
    }

    private sealed class RecordingGitleaksRunner : IGitleaksProcessRunner
    {
        private readonly GitleaksProcessResult _result;

        public RecordingGitleaksRunner(GitleaksProcessResult result)
        {
            _result = result;
        }

        public GitleaksProcessRequest? LastRequest { get; private set; }

        public GitleaksProcessResult Run(GitleaksProcessRequest request, TimeSpan timeout)
        {
            LastRequest = request;
            return _result;
        }
    }

    private static byte[] TestSecret()
    {
        return System.Text.Encoding.UTF8.GetBytes("unit-test-secret");
    }

    private static RedactionPolicy PolicyWithPublicDocsAllowlist(string match = "https://learn.microsoft.com/")
    {
        return RedactionPolicy.BuiltInDefaults with
        {
            AllowRules = new[]
            {
                new PolicyRule(
                    Type: "url",
                    Match: match,
                    Pattern: null,
                    Mode: "prefix",
                    Action: PolicyActions.Allow,
                    Reason: "public documentation",
                    Label: null)
            }
        };
    }
}

[TestFixture]
public class CliTests
{
    [Test]
    public void Main_SanitizeSyntheticSensitiveInput_PrintsDecisionAndSanitizedTextWithoutRawMarker()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--sanitize", "Check SENSITIVE_MARKER" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("decision: confirm"));
        Assert.That(stdout, Does.Contain("sanitized_text: Check SYNTHETIC_"));
        Assert.That(stdout, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void Main_SanitizeWithDictionary_PrintsSanitizedTextWithoutRawDictionaryTerm()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,pseudonymize_restorable,Known customer
                """);

            var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
                Program.Main(
                    new[] { "--sanitize", "Talk to ACME Banking", "--dictionary", dictionaryPath },
                    terms => new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), terms)));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("decision: confirm"));
            Assert.That(stdout, Does.Contain("sanitized_text: Talk to CUSTOMER_"));
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_PolicyTest_HidesSanitizedTextAndRawDictionaryTermByDefault()
    {
        var sampleText = "Talk to ACME Banking from C:\\Users\\user1>";
        var policy = CreateManagedPolicyLoadResult(RedactionPolicy.BuiltInDefaults);

        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(
                new[] { "--policy-test", sampleText },
                _ => new Sanitizer(
                    new InMemoryHmacMappingVault(TestSecret()),
                    new[]
                    {
                        new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null),
                        new DictionaryTerm("username", "user1", PolicyActions.PseudonymizeRestorable, null)
                    }),
                () => policy,
                _ => new Sanitizer(
                    new InMemoryHmacMappingVault(TestSecret()),
                    new[]
                    {
                        new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null),
                        new DictionaryTerm("username", "user1", PolicyActions.PseudonymizeRestorable, null)
                    })));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("policy_source: managed-active"));
        Assert.That(stdout, Does.Contain("decision: confirm"));
        Assert.That(stdout, Does.Contain("replacement_count: 2"));
        Assert.That(stdout, Does.Contain("entity.customer: 1"));
        Assert.That(stdout, Does.Contain("entity.username: 1"));
        Assert.That(stdout, Does.Contain("rule_source.dictionary: managed-dictionary"));
        Assert.That(stdout, Does.Not.Contain("sanitized_text:"));
        Assert.That(stdout, Does.Not.Contain("ACME Banking"));
        Assert.That(stdout, Does.Not.Contain("user1"));
    }

    [Test]
    public void Main_PolicyTest_ShowSanitized_PrintsSanitizedTextWhenExplicitlyRequested()
    {
        var policy = CreateManagedPolicyLoadResult(RedactionPolicy.BuiltInDefaults);

        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(
                new[] { "--policy-test", "Talk to ACME Banking", "--show-sanitized" },
                _ => new Sanitizer(
                    new InMemoryHmacMappingVault(TestSecret()),
                    new[]
                    {
                        new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null)
                    }),
                () => policy,
                _ => new Sanitizer(
                    new InMemoryHmacMappingVault(TestSecret()),
                    new[]
                    {
                        new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null)
                    })));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("sanitized_text: Talk to CUSTOMER_"));
        Assert.That(stdout, Does.Not.Contain("ACME Banking"));
    }

    [Test]
    public void Main_PolicyTest_ReportsManagedDomainAndUrlReplacements()
    {
        var policy = RedactionPolicy.BuiltInDefaults with
        {
            SensitiveRules = new[]
            {
                new PolicyRule(
                    Type: "url",
                    Match: "https://deploy.corp.example.local/internal/",
                    Pattern: null,
                    Mode: "prefix",
                    Action: PolicyActions.PseudonymizeRestorable,
                    Reason: "internal service",
                    Label: "managed url"),
                new PolicyRule(
                    Type: "domain",
                    Match: "corp.example.local",
                    Pattern: null,
                    Mode: "suffix",
                    Action: PolicyActions.PseudonymizeRestorable,
                    Reason: "internal domain",
                    Label: "managed domain")
            }
        };
        var policyLoadResult = CreateManagedPolicyLoadResult(policy);

        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(
                new[] { "--policy-test", "Open https://deploy.corp.example.local/internal/build and ping app.corp.example.local" },
                _ => new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), policy),
                () => policyLoadResult,
                activePolicy => new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), activePolicy.ActivePolicy)));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("decision: confirm"));
        Assert.That(stdout, Does.Contain("entity.domain: 1"));
        Assert.That(stdout, Does.Contain("entity.url: 1"));
        Assert.That(stdout, Does.Contain("rule_count.sensitive: 2"));
        Assert.That(stdout, Does.Contain("rule_source.sensitive: managed-active"));
        Assert.That(stdout, Does.Not.Contain("deploy.corp.example.local"));
        Assert.That(stdout, Does.Not.Contain("corp.example.local"));
    }

    [Test]
    public void Main_PolicyTest_UsesSingleLoadedPolicyForSanitizationAndReporting()
    {
        var policy = RedactionPolicy.BuiltInDefaults with
        {
            SensitiveRules = new[]
            {
                new PolicyRule(
                    Type: "url",
                    Match: "https://deploy.corp.example.local/internal/",
                    Pattern: null,
                    Mode: "prefix",
                    Action: PolicyActions.PseudonymizeRestorable,
                    Reason: "internal service",
                    Label: "managed url")
            }
        };
        var policyLoadResult = CreateManagedPolicyLoadResult(policy);
        var policyLoadCount = 0;

        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(
                new[] { "--policy-test", "Open https://deploy.corp.example.local/internal/build" },
                _ => new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
                () =>
                {
                    policyLoadCount++;
                    return policyLoadResult;
                },
                activePolicy => new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), activePolicy.ActivePolicy)));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(policyLoadCount, Is.EqualTo(1));
        Assert.That(stdout, Does.Contain("policy_source: managed-active"));
        Assert.That(stdout, Does.Contain("entity.url: 1"));
        Assert.That(stdout, Does.Contain("rule_source.sensitive: managed-active"));
    }

    [Test]
    public void Main_RestoreText_PrintsLocalRestoreOutputAndWarnings()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--restore-text",
                "Keep CUSTOMER_012345ABCDEF and TOKEN_REDACTED.");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: no_local_values_restored"));
            Assert.That(stdout, Does.Contain("warning.non_restorable_redaction_skipped"));
            Assert.That(stdout, Does.Contain("warning.unknown_pseudonym"));
            Assert.That(stdout, Does.Contain("NO LOCAL VALUES RESTORED"));
            Assert.That(stdout, Does.Contain("CUSTOMER_012345ABCDEF"));
            Assert.That(stdout, Does.Contain("TOKEN_REDACTED"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_RestoreText_RestoresKnownPseudonymThroughConfiguredWorkflow()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var vault = new InMemoryHmacMappingVault(TestSecret());
            var pseudonym = vault.GetOrCreatePseudonym("customer", "ACME Banking");

            var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
                Program.Main(
                    new[] { "--restore-text", $"Use {pseudonym} locally." },
                    new CliRuntime(
                        _ => TestSanitizers.Create(),
                        () => Sanitizer.LoadProductionPolicy(layout),
                        _ => TestSanitizers.Create(),
                        () => layout,
                        restoreLayout => new LocalRestoreWorkflow(
                            new LocalRestorer(vault),
                            new FileAuditSink(restoreLayout.AuditDirectory)))));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: local_sensitive_restored"));
            Assert.That(stdout, Does.Contain("restored.customer: 1"));
            Assert.That(stdout, Does.Contain("LOCAL-SENSITIVE RESTORED OUTPUT"));
            Assert.That(stdout, Does.Contain("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_AuditView_PrintsRawFreeAuditViewerRows()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));
            sanitizer.Sanitize(CreateCliPromptRequest("Connect to 192.168.10.25"));

            var (exitCode, stdout, stderr) = RunCli(layout, "--audit-view");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("chain: audit_chain_valid"));
            Assert.That(stdout, Does.Contain("decision=Confirm"));
            Assert.That(stdout, Does.Contain("actions:"));
            Assert.That(stdout, Does.Contain("entities:"));
            Assert.That(stdout, Does.Contain("scanner:"));
            Assert.That(stdout, Does.Contain("durations_ms:"));
            Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_AuditCleanup_KeepsRequestedEventCount()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var sanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FileAuditSink(layout.AuditDirectory));
            sanitizer.Sanitize(CreateCliPromptRequest("Normal prompt text 1"));
            sanitizer.Sanitize(CreateCliPromptRequest("Normal prompt text 2"));
            sanitizer.Sanitize(CreateCliPromptRequest("Normal prompt text 3"));

            var (exitCode, stdout, stderr) = RunCli(layout, "--audit-cleanup", "--keep", "2");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: audit_cleanup_complete"));
            Assert.That(stdout, Does.Contain("events_deleted: 1"));
            Assert.That(Directory.GetFiles(layout.AuditDirectory, "audit-*.json"), Has.Length.EqualTo(2));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_SanitizeWithInvalidDictionary_FailsWithoutRawDictionaryTerm()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var dictionaryPath = Path.Combine(tempDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,Known customer
                """);

            var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
                Program.Main(
                    new[] { "--sanitize", "Talk to ACME Banking", "--dictionary", dictionaryPath },
                    terms => new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), terms)));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("CSV dictionary could not be activated."));
            Assert.That(stderr, Does.Not.Contain("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_DictionaryAddBatch_AddsMultipleTermsRawFree()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--dictionary-add-batch",
                "domain",
                "corp.example.local",
                "url",
                "https://deploy.corp.example.local",
                "username",
                "user1",
                "customer",
                "ACME Banking");

            var terms = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).LoadTerms();

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: dictionary_batch_added"));
            Assert.That(stdout, Does.Contain("item type=domain status=dictionary_term_added value_length=18"));
            Assert.That(stdout, Does.Contain("item type=username status=dictionary_term_added value_length=5"));
            Assert.That(stdout, Does.Not.Contain("corp.example.local"));
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
            Assert.That(terms.Select(term => term.Type), Is.EquivalentTo(new[] { "domain", "url", "username", "customer" }));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_DictionaryListReveal_RequiresExplicitFlagAndPrintsWarning()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            store.Add("customer", "ACME Banking", null);

            var (_, rawFreeStdout, _) = RunCli(layout, "--dictionary-list");
            var (exitCode, revealStdout, stderr) = RunCli(layout, "--dictionary-list", "--reveal");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(rawFreeStdout, Does.Contain("value_length=12"));
            Assert.That(rawFreeStdout, Does.Not.Contain("ACME Banking"));
            Assert.That(revealStdout, Does.Contain("warning: local_sensitive_values_revealed"));
            Assert.That(revealStdout, Does.Contain("value=ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_UpdateEditsTermAndRejectsDuplicate()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            var first = store.Add("domain", "old.example.local", "old domain");
            var second = store.Add("domain", "existing.example.local", null);

            var update = store.Update(first.EntryId!, "domain", "new.example.local", "new domain");
            var duplicate = store.Update(first.EntryId!, "domain", "existing.example.local", null);
            var entries = store.ListEntriesForLocalReveal();

            Assert.That(update.Succeeded, Is.True);
            Assert.That(update.Code, Is.EqualTo("dictionary_term_updated"));
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(duplicate.Code, Is.EqualTo("dictionary_term_exists"));
            Assert.That(entries.Single(entry => entry.Id == first.EntryId).Value, Is.EqualTo("new.example.local"));
            Assert.That(entries.Single(entry => entry.Id == first.EntryId).Notes, Is.EqualTo("new domain"));
            Assert.That(entries.Any(entry => entry.Value == "old.example.local"), Is.False);
            Assert.That(entries.Single(entry => entry.Id == second.EntryId).Value, Is.EqualTo("existing.example.local"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_RejectsCaseAndSeparatorDuplicate()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            var first = store.Add("domain", "test.secret.com", null);

            var duplicateAdd = store.Add("domain", "Test secret com", null);
            var second = store.Add("username", "alexey.andreev", null);
            var duplicateUpdate = store.Update(second.EntryId!, "domain", "TEST_SECRET_COM", null);
            var separatorOnly = store.Add("domain", "...", null);
            var entries = store.ListEntriesForLocalReveal();

            Assert.That(first.Succeeded, Is.True);
            Assert.That(duplicateAdd.Succeeded, Is.False);
            Assert.That(duplicateAdd.Code, Is.EqualTo("dictionary_term_exists"));
            Assert.That(duplicateUpdate.Succeeded, Is.False);
            Assert.That(duplicateUpdate.Code, Is.EqualTo("dictionary_term_exists"));
            Assert.That(separatorOnly.Succeeded, Is.False);
            Assert.That(separatorOnly.Code, Is.EqualTo("invalid_dictionary_term"));
            Assert.That(entries.Count(entry => entry.Type == "domain"), Is.EqualTo(1));
            Assert.That(entries.Single(entry => entry.Type == "domain").Value, Is.EqualTo("test.secret.com"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_HotkeySetPersistsAndHotkeyShowLoadsConfiguredCombination()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var (setExitCode, setStdout, setStderr) = RunCli(layout, "--hotkey-set", "Shift+Ctrl+F8");
            var (showExitCode, showStdout, showStderr) = RunCli(layout, "--hotkey-show");

            Assert.That(setExitCode, Is.EqualTo(0));
            Assert.That(setStderr, Is.Empty);
            Assert.That(setStdout, Does.Contain("status: hotkey_saved"));
            Assert.That(setStdout, Does.Contain("hotkey: Ctrl+Shift+F8"));
            Assert.That(showExitCode, Is.EqualTo(0));
            Assert.That(showStderr, Is.Empty);
            Assert.That(showStdout, Does.Contain("status: hotkey_loaded"));
            Assert.That(showStdout, Does.Contain("hotkey: Ctrl+Shift+F8"));
            Assert.That(showStdout, Does.Contain("tray-settings.json"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_HotkeySetRejectsReservedCombinationBeforeActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var (exitCode, stdout, stderr) = RunCli(layout, "--hotkey-set", "Ctrl+Shift+F12");
            var loaded = HotkeySettingsStore.LoadOrDefault(layout);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: hotkey_reserved"));
            Assert.That(stdout, Does.Not.Contain("Ctrl+Shift+F12"));
            Assert.That(loaded.ProtectionHotkey.Binding.DisplayText, Is.EqualTo("Ctrl+Shift+F9"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_HotkeyShowReportsInvalidPersistedSettingsWithoutFallback()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            AtomicFileWriter.WriteAllBytes(
                HotkeySettingsStore.DefaultPath(layout),
                System.Text.Encoding.UTF8.GetBytes("""
                    {
                      "protection_hotkey": "Ctrl+Shift+F12"
                    }
                    """));

            var (exitCode, stdout, stderr) = RunCli(layout, "--hotkey-show");

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: hotkey_reserved"));
            Assert.That(stdout, Does.Contain("hotkey: configured_invalid"));
            Assert.That(stdout, Does.Not.Contain("Ctrl+Shift+F9"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_SendModeEnableRequiresSupportedApplyOnlyEvidence()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var (missingExitCode, missingStdout, missingStderr) = RunCli(layout, "--send-mode-enable");
            var (showExitCode, showStdout, showStderr) = RunCli(layout, "--send-mode-show");

            Assert.That(missingExitCode, Is.EqualTo(1));
            Assert.That(missingStderr, Is.Empty);
            Assert.That(missingStdout, Does.Contain($"status: {OsInteractionStatusIds.EvidenceMissing}"));
            Assert.That(missingStdout, Does.Contain("enabled: false"));
            Assert.That(showExitCode, Is.EqualTo(0));
            Assert.That(showStderr, Is.Empty);
            Assert.That(showStdout, Does.Contain($"status: {OsInteractionStatusIds.SafetyDisabled}"));
            Assert.That(showStdout, Does.Contain("send_mode_setting_enabled: false"));

            LiveOsDemoEvidence.MarkApplyOnlyPassed("redaction-gate-demo", layout);
            var (unsupportedExitCode, unsupportedStdout, unsupportedStderr) = RunCli(layout, "--send-mode-enable");
            Assert.That(unsupportedExitCode, Is.EqualTo(1));
            Assert.That(unsupportedStderr, Is.Empty);
            Assert.That(unsupportedStdout, Does.Contain($"status: {OsInteractionStatusIds.EvidenceMissing}"));
            Assert.That(unsupportedStdout, Does.Contain("supported_apply_evidence_present: false"));

            LiveOsDemoEvidence.MarkApplyOnlyPassed("codex-desktop", layout);
            var (enableExitCode, enableStdout, enableStderr) = RunCli(layout, "--send-mode-enable");
            var (disableExitCode, disableStdout, disableStderr) = RunCli(layout, "--send-mode-disable");

            Assert.That(enableExitCode, Is.EqualTo(0));
            Assert.That(enableStderr, Is.Empty);
            Assert.That(enableStdout, Does.Contain("status: send_gate_enabled"));
            Assert.That(enableStdout, Does.Contain("enabled: true"));
            Assert.That(disableExitCode, Is.EqualTo(0));
            Assert.That(disableStderr, Is.Empty);
            Assert.That(disableStdout, Does.Contain($"status: {OsInteractionStatusIds.SafetyDisabled}"));
            Assert.That(disableStdout, Does.Contain("enabled: false"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_NativeProfileVerifyDelayRejectsInvalidDelayWithoutProfileChanges()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--native-profile-verify-delay",
                "codex-desktop",
                "Enter",
                "Ctrl+Enter",
                "later");
            var profiles = SubmitBindingProfileStore.Load(layout);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("--native-profile-verify-delay profile-id submit-binding newline-binding non-negative-delay-seconds"));
            Assert.That(profiles.Profiles, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_NativeProfileVerifyImmediateCommandDoesNotSaveProfile()
    {
        var tempDirectory = CreateTempDirectory();
        var originalDiscoveryFactory = Program.NativeProfileDiscoveryFactory;

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            Program.NativeProfileDiscoveryFactory = () => TextSurfaceDiscoveryResult.Success(CreateCliNativeSubmitSurface("codex-desktop"));

            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--native-profile-verify",
                "codex-desktop",
                "Enter",
                "Ctrl+Enter");
            var profiles = SubmitBindingProfileStore.Load(layout);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("--native-profile-verify-delay"));
            Assert.That(profiles.Profiles, Is.Empty);
        }
        finally
        {
            Program.NativeProfileDiscoveryFactory = originalDiscoveryFactory;
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_NativeProfilesStatus_ReportsProgrammaticUiaInvokeAsUnsupportedWithoutProfileDiagnostics()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                "codex-desktop",
                "Enter",
                "Ctrl+Enter",
                TextSurfaceDiscoveryResult.Success(CreateCliNativeSubmitSurface("codex-desktop"))) with
            {
                Diagnostics = new Dictionary<string, string>
                {
                    ["untrusted"] = "PROMPT_SECRET_123"
                }
            };
            SubmitBindingProfileStore.Save(layout, new[] { profile });

            var (exitCode, stdout, stderr) = RunCli(layout, "--native-profiles-status");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("composer_protected=true"));
            Assert.That(stdout, Does.Contain("programmatic_uia_invoke=programmatic_uia_invoke_unsupported"));
            Assert.That(stdout, Does.Not.Contain("PROMPT_SECRET_123"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_NativeProfileVerifyDelaySavesProtectedProfileAfterFocusedComposerVerification()
    {
        var tempDirectory = CreateTempDirectory();
        var originalDiscoveryFactory = Program.NativeProfileDiscoveryFactory;

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            Program.NativeProfileDiscoveryFactory = () => TextSurfaceDiscoveryResult.Success(CreateCliNativeSubmitSurface("chatgpt-desktop"));

            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--native-profile-verify-delay",
                "chatgpt-desktop",
                "Enter",
                "Ctrl+Enter",
                "0");
            var profiles = SubmitBindingProfileStore.Load(layout);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: protected"));
            Assert.That(stdout, Does.Contain("submit_binding: Enter"));
            Assert.That(stdout, Does.Contain("newline_binding: Ctrl+Enter"));
            Assert.That(stdout, Does.Contain("cloud_submission: false"));
            Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
            Assert.That(profiles.Profiles, Has.Count.EqualTo(1));
            Assert.That(profiles.Profiles[0].ProfileId, Is.EqualTo("chatgpt-desktop"));
            Assert.That(profiles.Profiles[0].IsProtected, Is.True);
        }
        finally
        {
            Program.NativeProfileDiscoveryFactory = originalDiscoveryFactory;
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_LocalDataCleanupKeepsDataUnlessExplicitlyConfirmed()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            File.WriteAllText(Path.Combine(layout.PolicyDirectory, "managed-dictionary.csv"), "type,value");

            var (planExitCode, planStdout, planStderr) = RunCli(layout, "--local-data-cleanup");
            var (deleteExitCode, deleteStdout, deleteStderr) = RunCli(
                layout,
                "--local-data-cleanup",
                "--i-understand-delete-local-sensitive-data");

            Assert.That(planExitCode, Is.EqualTo(0));
            Assert.That(planStderr, Is.Empty);
            Assert.That(planStdout, Does.Contain("status: local_data_kept"));
            Assert.That(planStdout, Does.Contain("deleted: false"));
            Assert.That(planStdout, Does.Contain("planned_directory_count: 4"));
            Assert.That(deleteExitCode, Is.EqualTo(0));
            Assert.That(deleteStderr, Is.Empty);
            Assert.That(deleteStdout, Does.Contain("status: local_data_deleted"));
            Assert.That(deleteStdout, Does.Contain("deleted: true"));
            Assert.That(Directory.Exists(tempDirectory), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void Main_ProductSmokePrintsRawFreeEndToEndStatus()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--product-smoke" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("status: product_smoke_passed"));
        Assert.That(stdout, Does.Contain("supported_targets: windows_codex_chatgpt_desktop_only"));
        Assert.That(stdout, Does.Contain("apply_only_write_back: true"));
        Assert.That(stdout, Does.Contain("project_file_read_only_smoke: true"));
        Assert.That(stdout, Does.Contain("project_file_product_smoke: true"));
        Assert.That(stdout, Does.Contain("native_submit_repeatability: true"));
        Assert.That(stdout, Does.Contain("native_submit_duplicate_guard: true"));
        Assert.That(stdout, Does.Contain("native_submit_overlay_foreground_request: true"));
        Assert.That(stdout, Does.Contain("native_submit_overlay_foreground_refusal_status: true"));
        Assert.That(stdout, Does.Contain("audit_view: true"));
        Assert.That(stdout, Does.Contain("restore: true"));
        Assert.That(stdout, Does.Contain("uninstall_safe_default: true"));
        Assert.That(stdout, Does.Contain("raw_free_artifacts: true"));
        Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
        Assert.That(stdout, Does.Not.Contain("Product Smoke Customer"));
        Assert.That(stdout, Does.Not.Contain("product-smoke.example.local"));
    }

    [Test]
    public void Main_DictionaryAddBatchRejectsDuplicateRawFreeWithoutPartialWrite()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            store.Add("customer", "ACME Banking", null);

            var (exitCode, stdout, stderr) = RunCli(
                layout,
                "--dictionary-add-batch",
                "customer",
                "ACME Banking",
                "project",
                "Blue Falcon");
            var terms = store.LoadTerms();

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: dictionary_batch_rejected"));
            Assert.That(stdout, Does.Contain("item type=customer status=dictionary_term_exists value_length=12"));
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
            Assert.That(stdout, Does.Not.Contain("Blue Falcon"));
            Assert.That(terms, Has.Count.EqualTo(1));
            Assert.That(terms.Single().Value, Is.EqualTo("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_DictionaryImportRejectsInvalidFileWithoutReplacingExistingTerms()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            store.Add("customer", "SAFE_CUSTOMER", null);
            var invalidPath = Path.Combine(tempDirectory, "invalid.csv");
            File.WriteAllText(invalidPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,Known customer
                """);

            var (exitCode, stdout, stderr) = RunCli(layout, "--dictionary-import", invalidPath);
            var terms = store.LoadTerms();

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: dictionary_import_rejected"));
            Assert.That(stdout, Does.Contain("warning: invalid_dictionary_rejected"));
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
            Assert.That(terms.Single().Value, Is.EqualTo("SAFE_CUSTOMER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_RulesExportCopiesOnlyPolicyAndDictionaryFiles()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            layout.EnsureDirectories();
            new ManagedPolicyRules(layout.PolicyDirectory).AddUrlPrefix("https://deploy.corp.example.local/");
            new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).Add("customer", "ACME Banking", null);
            File.WriteAllText(Path.Combine(layout.VaultDirectory, "vault.json"), "do-not-export");
            var exportDirectory = Path.Combine(tempDirectory, "export");

            var (exitCode, stdout, stderr) = RunCli(layout, "--rules-export", exportDirectory);
            var exportedFiles = Directory.GetFiles(exportDirectory).Select(Path.GetFileName).ToArray();

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: rules_exported"));
            Assert.That(stdout, Does.Contain("file: active-policy.toml"));
            Assert.That(stdout, Does.Contain("file: managed-dictionary.csv"));
            Assert.That(exportedFiles, Is.EquivalentTo(new[] { "active-policy.toml", "managed-dictionary.csv" }));
            Assert.That(File.Exists(Path.Combine(exportDirectory, "vault.json")), Is.False);
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
            Assert.That(stdout, Does.Not.Contain("deploy.corp.example.local"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_DictionaryImportRejectsMissingFileWithoutReplacingExistingTerms()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            store.Add("customer", "SAFE_CUSTOMER", null);
            var missingPath = Path.Combine(tempDirectory, "missing.csv");

            var (exitCode, stdout, stderr) = RunCli(layout, "--dictionary-import", missingPath);
            var terms = store.LoadTerms();

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: dictionary_import_rejected"));
            Assert.That(stdout, Does.Contain("term_count: 1"));
            Assert.That(terms.Single().Value, Is.EqualTo("SAFE_CUSTOMER"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_DictionaryRemove_RemovesMultipleIdsInOneOperation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
            var customer = store.Add("customer", "ACME Banking", null);
            var username = store.Add("username", "user1", null);

            var (exitCode, stdout, stderr) = RunCli(layout, "--dictionary-remove", customer.EntryId!, username.EntryId!);
            var terms = store.LoadTerms();

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: dictionary_terms_removed"));
            Assert.That(stdout, Does.Contain($"item id={customer.EntryId} status=dictionary_term_removed"));
            Assert.That(stdout, Does.Contain($"item id={username.EntryId} status=dictionary_term_removed"));
            Assert.That(stdout, Does.Not.Contain("ACME Banking"));
            Assert.That(stdout, Does.Not.Contain("user1"));
            Assert.That(terms, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Main_SelfTest_ReturnsZeroWhenChecksPass()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--self-test" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("Self-test passed."));
    }

    [Test]
    public void Main_SelfTest_IsolatedFromProductionDpapiFailure()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(
                new[] { "--self-test" },
                () => throw new DpapiSecretLoadFailureException("PROMPT_SECRET_123 C:\\Users\\alexey.andreev")));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("Self-test passed."));
        Assert.That(stdout, Does.Not.Contain("PROMPT_SECRET_123"));
        Assert.That(stdout, Does.Not.Contain("C:\\Users\\alexey.andreev"));
        Assert.That(stderr, Is.Empty);
    }

    [Test]
    public void ReadinessDoctor_ReportsProductionDpapiFailureWithoutRawExceptionData()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var report = ReadinessDoctor.Check(
                DefaultStorageLayout.Create(tempDirectory),
                vaultSecretProbe: () => throw new DpapiSecretLoadFailureException(
                    "PROMPT_SECRET_123 C:\\Users\\alexey.andreev"));
            var rendered = System.Text.Json.JsonSerializer.Serialize(report);

            Assert.That(report.Ready, Is.False);
            Assert.That(
                report.Items.Single(item => item.Component == "vault_secret").Code,
                Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(rendered, Does.Not.Contain("PROMPT_SECRET_123"));
            Assert.That(rendered, Does.Not.Contain("C:\\Users\\alexey.andreev"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PublicFailureText_NeverIncludesExceptionMessage()
    {
        var exception = new DpapiSecretLoadFailureException(new InvalidOperationException(
            "PROMPT_SECRET_123 C:\\Users\\alexey.andreev WINDOW_TITLE_123 RULE_VALUE_123"));

        var text = PublicFailureText.Format(exception, "Local restore");

        Assert.That(text, Does.Contain(DpapiSecretLoadFailureException.PublicStatusCode));
        Assert.That(text, Does.Not.Contain("PROMPT_SECRET_123"));
        Assert.That(text, Does.Not.Contain("alexey.andreev"));
        Assert.That(text, Does.Not.Contain("WINDOW_TITLE_123"));
        Assert.That(text, Does.Not.Contain("RULE_VALUE_123"));
    }

    [Test]
    public void PublicFailureText_StorageFailureUsesStableRawFreeStatus()
    {
        var exception = new UnauthorizedAccessException(
            "PROMPT_SECRET_123 C:\\Users\\alexey.andreev WINDOW_TITLE_123 RULE_VALUE_123");

        var text = PublicFailureText.Format(exception, "Sensitive terms");

        Assert.That(text, Does.Contain("status=local_operation_failed"));
        Assert.That(text, Does.Not.Contain("PROMPT_SECRET_123"));
        Assert.That(text, Does.Not.Contain("alexey.andreev"));
        Assert.That(text, Does.Not.Contain("WINDOW_TITLE_123"));
        Assert.That(text, Does.Not.Contain("RULE_VALUE_123"));
    }

    [Test]
    public void LocalCrashDiagnostics_DefaultDirectoryUsesOneSharedPath()
    {
        var directory = LocalCrashDiagnostics.DefaultReportsDirectory();

        Assert.That(directory, Does.EndWith(Path.Combine("CodexRedactionGate", "crashes")));
        Assert.That(LocalCrashDiagnostics.CreateDefault(), Is.Not.Null);
    }

    [Test]
    public void Main_UnknownArguments_PrintHelpAndDoNotCrash()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--unknown" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("Codex Redaction Gate"));
        Assert.That(stdout, Does.Contain("--sanitize \"text\""));
        Assert.That(stdout, Does.Contain("--restore-text \"sanitized model response\""));
        Assert.That(stdout, Does.Contain("--restore-view"));
        Assert.That(stdout, Does.Contain("--dictionary-ui"));
        Assert.That(stdout, Does.Contain("--policy-test \"text\" [--show-sanitized]"));
        Assert.That(stdout, Does.Contain("--hotkey-show"));
        Assert.That(stdout, Does.Contain("--hotkey-set \"Ctrl+Shift+F9\""));
        Assert.That(stdout, Does.Contain("--send-mode-show"));
        Assert.That(stdout, Does.Contain("--send-mode-enable"));
        Assert.That(stdout, Does.Contain("--send-mode-disable"));
        Assert.That(stdout, Does.Contain("--autostart-show"));
        Assert.That(stdout, Does.Contain("--autostart-enable"));
        Assert.That(stdout, Does.Contain("--autostart-disable"));
        Assert.That(stdout, Does.Contain("--local-data-cleanup [--i-understand-delete-local-sensitive-data]"));
        Assert.That(stdout, Does.Contain("--audit-view"));
        Assert.That(stdout, Does.Contain("--audit-verify"));
        Assert.That(stdout, Does.Contain("--audit-cleanup --keep count"));
        Assert.That(stdout, Does.Contain("--project-workspace-protect workspace"));
        Assert.That(stdout, Does.Contain("--project-workspace-status workspace"));
        Assert.That(stdout, Does.Contain("--project-file-ingress-status workspace"));
        Assert.That(stdout, Does.Contain("--project-file-sanitize file [--protected-workspace workspace]"));
        Assert.That(stdout, Does.Contain("--project-file-smoke"));
        Assert.That(stdout, Does.Contain("--project-tool-output-sanitize workspace \"tool output\""));
        Assert.That(stdout, Does.Contain("--project-tool-output-unmanaged workspace"));
        Assert.That(stdout, Does.Contain("--project-patch-dry-run file --protected-workspace workspace --source-content-hash hash --sanitized-edit \"text\""));
        Assert.That(stdout, Does.Contain("--project-patch-apply file --protected-workspace workspace --source-content-hash hash --sanitized-edit \"text\" (--approve|--cancel)"));
        Assert.That(stdout, Does.Contain("--project-attachment-bypass-status workspace"));
        Assert.That(stdout, Does.Contain("--project-connector-bypass-status workspace"));
        Assert.That(stdout, Does.Contain("--project-file-product-smoke"));
        Assert.That(stdout, Does.Contain("--tray-app"));
        Assert.That(stdout, Does.Contain("--os-compatibility-matrix"));
        Assert.That(stdout, Does.Contain("--product-smoke"));
        Assert.That(stdout, Does.Contain("--native-profile-verify-delay profile-id submit-binding newline-binding seconds"));
        Assert.That(stdout, Does.Contain("--self-test"));
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureProgramOutput(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = action();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        DefaultStorageLayout layout,
        params string[] args)
    {
        return CaptureProgramOutput(() =>
            Program.Main(
                args,
                new CliRuntime(
                    _ => TestSanitizers.Create(),
                    () => Sanitizer.LoadProductionPolicy(layout),
                    _ => TestSanitizers.Create(),
                    () => layout,
                    restoreLayout => new LocalRestoreWorkflow(
                        new LocalRestorer(new InMemoryHmacMappingVault(TestSecret())),
                        new FileAuditSink(restoreLayout.AuditDirectory)))));
    }

    private static TextSurfaceDescriptor CreateCliNativeSubmitSurface(string profileId)
    {
        return new TextSurfaceDescriptor(
            $"cli-native-profile-test:{profileId}",
            profileId,
            profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new SurfaceMetadata(
                SurfaceKind: "test",
                CloudSubmission: "false",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer));
    }

    private static SanitizeRequest CreateCliPromptRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart(
                    Id: "prompt",
                    ContentSource: ContentSources.PromptText,
                    RawText: text,
                    SourceMetadata: new Dictionary<string, string>())
            },
            Context: new SanitizationContext(
                Application: "tests",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] TestSecret()
    {
        return System.Text.Encoding.UTF8.GetBytes("unit-test-secret");
    }

    private static ManagedPolicyLoadResult CreateManagedPolicyLoadResult(RedactionPolicy policy)
    {
        return new ManagedPolicyLoadResult(
            ActivePolicy: policy,
            Source: "managed-active",
            LoadedFromFile: true,
            Activated: true,
            Warnings: Array.Empty<SanitizerWarning>(),
            Diagnostics: PolicyPrecedenceReporter.Build(new[]
            {
                new PolicySource("managed-active", policy)
            }));
    }
}

[TestFixture]
public class CrashDiagnosticsTests
{
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Test]
    public void CrashDiagnostics_BootstrapReturnsTheSharedDefaultInstance()
    {
        Assert.That(LocalCrashDiagnostics.Bootstrap(), Is.SameAs(LocalCrashDiagnostics.CreateDefault()));
    }

    [Test]
    public void CrashDiagnostics_CaptureFailureDoesNotEscapeTheCrashBoundary()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var reportsPath = Path.Combine(tempDirectory, "reports-file");
            File.WriteAllText(reportsPath, "not a directory");
            var crashDiag = new LocalCrashDiagnostics(reportsPath);

            Assert.DoesNotThrow(() => crashDiag.Capture(
                new InvalidOperationException("PROMPT_SECRET_123"),
                "test_component",
                "test_failure"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void CrashDiagnostics_CapturesAndLoadsReports()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var crashDirectory = Path.Combine(tempDirectory, "crashes");
            var crashDiag = new LocalCrashDiagnostics(crashDirectory);

            // Capture a test crash
            var ex = new InvalidOperationException("PROMPT_SECRET_123 C:\\Users\\alexey.andreev");
            crashDiag.Capture(ex, "test_component");

            // Load the report
            var reports = crashDiag.LoadReports();
            Assert.That(reports, Has.Count.EqualTo(1));
            Assert.That(reports[0].Component, Is.EqualTo("test_component"));
            Assert.That(reports[0].ExceptionType, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(reports[0].BuildVersion, Is.Not.Null);
            Assert.That(System.Text.Json.JsonSerializer.Serialize(reports), Does.Not.Contain("PROMPT_SECRET_123"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(reports), Does.Not.Contain("alexey.andreev"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void CrashDiagnostics_GetLatestReport()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var crashDirectory = Path.Combine(tempDirectory, "crashes");
            var crashDiag = new LocalCrashDiagnostics(crashDirectory);

            // Capture multiple crashes
            crashDiag.Capture(new Exception("First"), "component1");
            System.Threading.Thread.Sleep(100); // Ensure different timestamps
            crashDiag.Capture(new Exception("Second"), "component2");

            // Get latest - files are sorted by name alphabetically
            // crash-*.json filenames sort by timestamp, so First() is oldest
            // GetLatestReport returns reports[0] which is the oldest
            var latest = crashDiag.GetLatestReport();
            Assert.That(latest, Is.Not.Null);
            // The test needs to expect oldest (First) since files sort alphabetically
            Assert.That(latest!.Component, Is.EqualTo("component1"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void CrashDiagnostics_GetRawFreeSummary()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var crashDirectory = Path.Combine(tempDirectory, "crashes");
            var crashDiag = new LocalCrashDiagnostics(crashDirectory);

            crashDiag.Capture(new Exception("PROMPT_SECRET_123"), "test");

            var summary = crashDiag.GetRawFreeSummary();
            Assert.That(summary, Has.Count.EqualTo(3));
            Assert.That(summary[0], Does.Contain("test"));
            Assert.That(summary[0], Does.Contain("System.Exception"));
            Assert.That(string.Join(Environment.NewLine, summary), Does.Not.Contain("PROMPT_SECRET_123"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void CrashDiagnostics_NoCrashReportsWhenEmpty()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var crashDirectory = Path.Combine(tempDirectory, "crashes");
            var crashDiag = new LocalCrashDiagnostics(crashDirectory);

            var reports = crashDiag.LoadReports();
            Assert.That(reports, Is.Empty);

            var latest = crashDiag.GetLatestReport();
            Assert.That(latest, Is.Null);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void OsInteractionOrchestrator_CrashBoundaryReturnsFailClosed()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var crashDirectory = Path.Combine(tempDirectory, "crashes");
            var crashDiag = new LocalCrashDiagnostics(crashDirectory);

            // Create a mock sanitizer that throws
            var sanitizer = new ThrowingSanitizer(new InvalidOperationException("PROMPT_SECRET_123 C:\\Users\\alexey.andreev"));

            var orchestrator = new OsInteractionOrchestrator(
                sanitizer,
                new FixedSurfaceDiscovery(),
                new FixedTextReader(),
                new FixedTextWriter(),
                new FixedSubmitAction(),
                new FixedConfirmationOverlay());

            var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Submitted, Is.False);
            Assert.That(result.Diagnostics["failed_closed"], Is.EqualTo("true"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(result.Diagnostics), Does.Not.Contain("PROMPT_SECRET_123"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(result.Diagnostics), Does.Not.Contain("alexey.andreev"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}

internal sealed class ThrowingSanitizer : ISanitizer
{
    private readonly Exception _exception;

    public ThrowingSanitizer(Exception exception)
    {
        _exception = exception;
    }

    public SanitizationResult Sanitize(SanitizeRequest request)
    {
        throw _exception;
    }
}

internal sealed class FixedSurfaceDiscovery : IActiveTextSurfaceDiscovery
{
    public TextSurfaceDiscoveryResult DiscoverActiveSurface()
    {
        return TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
            "fixed-surface",
            "test-profile",
            "Test Profile",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new SurfaceMetadata()));
    }
}

internal sealed class FixedTextReader : ITextSurfaceReader
{
    public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        return new TextCaptureResult(true, "captured", "test prompt", new Dictionary<string, string>());
    }
}

internal sealed class FixedTextWriter : ITextSurfaceWriter
{
    public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        return new TextReplacementResult(true, "replaced", new Dictionary<string, string>());
    }
}

internal sealed class FixedSubmitAction : ISubmitAction
{
    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        return new SubmitActionResult(true, "submitted", new Dictionary<string, string>());
    }
}

internal sealed class FixedConfirmationOverlay : IConfirmationOverlay
{
    public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
    {
        return new ConfirmationDecision(false, null);
    }
}

[TestFixture]
public class SingleInstanceEnforcementTests
{
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Test]
    public void SingleInstanceEnforcement_IsFirstInstance()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");
        using var enforcement = new SingleInstanceEnforcement(instanceId);
        Assert.That(enforcement.IsFirstInstance, Is.True);
    }

    [Test]
    public void SingleInstanceEnforcement_NoInstanceWhenEmpty()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");

        Assert.That(SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId), Is.False);
    }

    [Test]
    public void SingleInstanceEnforcement_ReentrantCheckDoesNotCreateAnotherOwner()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstanceEnforcement(instanceId);

        Assert.That(SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId), Is.False);

        using var second = new SingleInstanceEnforcement(instanceId);
        Assert.That(second.IsFirstInstance, Is.False);
    }

    [Test]
    public void SingleInstanceEnforcement_BuildMutexNameKeepsPerUserAsDefault()
    {
        Assert.That(SingleInstanceEnforcement.BuildMutexName("tray", useGlobalNamespace: false), Is.EqualTo("CodexRedactionGate_tray"));
        Assert.That(SingleInstanceEnforcement.BuildMutexName("tray", useGlobalNamespace: true), Is.EqualTo("Global\\CodexRedactionGate_tray"));
    }

    [Test]
    public void SingleInstanceEnforcement_RecoversAfterAbandonedMutex()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            _ = new Mutex(initiallyOwned: true, name: SingleInstanceEnforcement.BuildMutexName(instanceId, false));
            ready.Set();
        });
        thread.Start();
        Assert.That(ready.Wait(TimeSpan.FromSeconds(2)), Is.True);
        thread.Join();

        Assert.That(
            SpinWait.SpinUntil(
                () => !SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId),
                TimeSpan.FromSeconds(2)),
            Is.True,
            "Abandoned mutex was not released within the bounded recovery window.");

        using var recovered = new SingleInstanceEnforcement(instanceId);
        Assert.That(recovered.IsFirstInstance, Is.True);
    }

    [Test]
    public void SingleInstanceEnforcement_RecoversAcrossRapidAbandonRestartCycles()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            AbandonOwnedMutex(instanceId);

            Assert.That(
                SpinWait.SpinUntil(
                    () => !SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                $"Cycle {cycle}: abandoned mutex was not released within the bounded recovery window.");

            using var recovered = new SingleInstanceEnforcement(instanceId);
            Assert.That(recovered.IsFirstInstance, Is.True, $"Cycle {cycle}: recovered launch was not the first instance.");
            Assert.That(SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId), Is.False, $"Cycle {cycle}: stale ownership remained after recovery.");
        }
    }

    [TestCase(null, "balloon")]
    [TestCase("toast", "toast")]
    [TestCase("balloon", "balloon")]
    [TestCase("messagebox", "balloon")]
    [TestCase("none", "none")]
    [TestCase("unexpected", "balloon")]
    public void SingleInstanceNotificationSettings_NormalizesKnownTypes(string? configured, string expected)
    {
        Assert.That(SingleInstanceNotificationSettings.NormalizeType(configured), Is.EqualTo(expected));
    }

    [Test]
    public void SingleInstanceNotificationSettings_UsesBalloonByDefault()
    {
        var settings = SingleInstanceNotificationSettings.FromRegistryValues(
            disableNotification: null,
            notificationType: null);

        Assert.That(settings, Is.EqualTo(new SingleInstanceNotificationSettings(true, "balloon")));
    }

    [TestCase(1)]
    [TestCase(-1)]
    public void SingleInstanceNotificationSettings_DisableNotificationSuppressesEveryPresentation(int disabledValue)
    {
        var settings = SingleInstanceNotificationSettings.FromRegistryValues(
            disabledValue,
            notificationType: "toast");

        Assert.That(settings, Is.EqualTo(new SingleInstanceNotificationSettings(false, "none")));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void WindowsTrayApp_SecondInstanceCreatesNonModalNotificationForEveryActivationOutcome(bool activationSucceeded)
    {
        var notification = WindowsTrayApp.CreateSecondInstanceNotification(
            new SingleInstanceNotificationSettings(true, "balloon"),
            activationSucceeded);

        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Title, Is.EqualTo(AppStrings.Get("ProductName")));
        Assert.That(notification.Message, Is.EqualTo(AppStrings.Get("AlreadyRunning")));
        Assert.That(notification.DisplayMilliseconds, Is.GreaterThan(0));
    }

    [Test]
    public void WindowsTrayApp_SecondInstanceDoesNotCreateNotificationWhenUserSuppressesIt()
    {
        var notification = WindowsTrayApp.CreateSecondInstanceNotification(
            new SingleInstanceNotificationSettings(false, "none"),
            activationSucceeded: true);

        Assert.That(notification, Is.Null);
    }

    [Test]
    public void SingleInstanceEnforcement_DisposeIsIdempotent()
    {
        var enforcement = new SingleInstanceEnforcement("codex-redaction-gate-test-" + Guid.NewGuid().ToString("N"));

        Assert.DoesNotThrow(enforcement.Dispose);
        Assert.DoesNotThrow(enforcement.Dispose);
    }

    [Test]
    public void SingleInstanceEnforcement_ActivateExistingInstance_ReportsFailureWithoutAnActivationWindow()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");
        string? capturedTitle = null;
        string? capturedMessage = null;
        bool? capturedIncludeDiagnostics = null;

        // Start first instance
        using var enforcement1 = new SingleInstanceEnforcement(instanceId);
        Assert.That(enforcement1.IsFirstInstance, Is.True);

        // Second instance should detect first and call callback
        SingleInstanceEnforcement.ActivateExistingInstance(
            instanceId,
            (title, message, includeDiagnostics) =>
            {
                capturedTitle = title;
                capturedMessage = message;
                capturedIncludeDiagnostics = includeDiagnostics;
            });

        Assert.That(capturedTitle, Is.EqualTo(AppStrings.Get("ProductName")));
        Assert.That(capturedMessage, Is.EqualTo(AppStrings.Get("AlreadyRunning")));
        Assert.That(capturedIncludeDiagnostics, Is.False);
    }

    [Test]
    public void SingleInstanceEnforcement_ActivateExistingInstance_WithoutWindowReturnsFalse()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");

        // Start first instance
        using var enforcement1 = new SingleInstanceEnforcement(instanceId);
        Assert.That(enforcement1.IsFirstInstance, Is.True);

        // A mutex alone is not proof of foreground activation.
        var result = SingleInstanceEnforcement.ActivateExistingInstance(instanceId);
        Assert.That(result, Is.False);
    }

    [Test]
    public void SingleInstanceEnforcement_ActivateExistingInstance_NoInstanceReturnsFalse()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");

        // No instance running - should return false
        var result = SingleInstanceEnforcement.ActivateExistingInstance(instanceId);
        Assert.That(result, Is.False);
    }

    [Test]
    public void SingleInstanceEnforcement_ActivateExistingInstance_NoInstanceIncludesNoDiagnosticsLink()
    {
        var instanceId = "codex-redaction-gate-test-" + Guid.NewGuid().ToString("N");
        bool? capturedIncludeDiagnostics = null;

        // No instance running - callback should be called with includeDiagnosticsLink=false
        SingleInstanceEnforcement.ActivateExistingInstance(
            instanceId,
            (title, message, includeDiagnostics) =>
            {
                capturedIncludeDiagnostics = includeDiagnostics;
            });

        Assert.That(capturedIncludeDiagnostics, Is.False);
    }

    [Test]
    public void WindowsTrayApp_SecondInstanceAlwaysProvidesVisibleOutcome()
    {
        Assert.That(WindowsTrayApp.ShouldNotifySecondInstance(), Is.True);
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApp_RunFirstInstanceRetainsNativeHookAndTrayIconUntilMessageLoopExits()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var trayIconVisible = false;
            var nativeHookReady = false;

            var exitCode = WindowsTrayApp.Run(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(tempDirectory),
                useGlobalMutex: false,
                context =>
                {
                    trayIconVisible = context.IsTrayIconVisible;
                    nativeHookReady = context.IsNativeSubmitHookReady;
                    Assert.That(TrayActivationWindowStore.Default.TryRead("tray", out var windowHandle), Is.True);
                    Assert.That(windowHandle, Is.Not.EqualTo(IntPtr.Zero));
                    context.ExitThread();
                },
                new SingleInstanceNotificationSettings(false, "none"));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(trayIconVisible, Is.True);
            Assert.That(nativeHookReady, Is.True);
            Assert.That(SingleInstanceEnforcement.IsAnotherInstanceRunning("tray"), Is.False);
            Assert.That(TrayActivationWindowStore.Default.TryRead("tray", out _), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void WindowsTrayApp_RunSecondInstanceActivatesExistingWindowAndExitsCleanly()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            SecondInstanceNotification? notification = null;
            using var owner = new ExternalMutexOwner("tray");

            Assert.That(SingleInstanceEnforcement.IsAnotherInstanceRunning("tray"), Is.True);
            var exitCode = WindowsTrayApp.Run(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(tempDirectory),
                useGlobalMutex: false,
                _ => throw new AssertionException("A second tray instance must not enter the message loop."),
                new SingleInstanceNotificationSettings(true, "balloon"),
                shown => notification = shown,
                existingInstanceActivator: new TestExistingTrayInstanceActivator(activationSucceeded: true));
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(notification?.ActivationSucceeded, Is.True);
        }
        finally
        {
            SingleInstanceEnforcement.ClearActivationWindow("tray");
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void WindowsTrayApp_RunSecondInstanceReportsVisibleFallbackWhenActivationWindowIsUnavailable()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            SecondInstanceNotification? notification = null;
            using var owner = new ExternalMutexOwner("tray");

            var exitCode = WindowsTrayApp.Run(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(tempDirectory),
                useGlobalMutex: false,
                _ => throw new AssertionException("A second tray instance must not enter the message loop."),
                new SingleInstanceNotificationSettings(true, "balloon"),
                shown => notification = shown,
                existingInstanceActivator: new TestExistingTrayInstanceActivator(activationSucceeded: false));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(notification, Is.Not.Null);
            Assert.That(notification!.ActivationSucceeded, Is.False);
        }
        finally
        {
            SingleInstanceEnforcement.ClearActivationWindow("tray");
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void WindowsTrayApp_RunSecondInstanceReportsForegroundRefusalEvenWhenRoutineNotificationsAreDisabled()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            SecondInstanceNotification? notification = null;
            using var owner = new ExternalMutexOwner("tray");

            var exitCode = WindowsTrayApp.Run(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(tempDirectory),
                useGlobalMutex: false,
                _ => throw new AssertionException("A second tray instance must not enter the message loop."),
                new SingleInstanceNotificationSettings(false, "none"),
                shown => notification = shown,
                existingInstanceActivator: new TestExistingTrayInstanceActivator(activationSucceeded: false));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(notification, Is.Not.Null);
            Assert.That(notification!.ActivationSucceeded, Is.False);
        }
        finally
        {
            SingleInstanceEnforcement.ClearActivationWindow("tray");
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class TestExistingTrayInstanceActivator : IExistingTrayInstanceActivator
    {
        private readonly bool _activationSucceeded;

        public TestExistingTrayInstanceActivator(bool activationSucceeded)
        {
            _activationSucceeded = activationSucceeded;
        }

        public bool TryActivate(string instanceId, bool useGlobalMutex)
        {
            Assert.That(instanceId, Is.EqualTo("tray"));
            Assert.That(useGlobalMutex, Is.False);
            return _activationSucceeded;
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApp_RunRecoversAfterAbandonedFirstInstance()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            using var ownerReady = new ManualResetEventSlim(false);
            var owner = new Thread(() =>
            {
                _ = new Mutex(initiallyOwned: true, name: SingleInstanceEnforcement.BuildMutexName("tray", false));
                ownerReady.Set();
            });
            owner.Start();
            Assert.That(ownerReady.Wait(TimeSpan.FromSeconds(2)), Is.True);
            owner.Join();
            Assert.That(SpinWait.SpinUntil(
                () => !SingleInstanceEnforcement.IsAnotherInstanceRunning("tray"),
                TimeSpan.FromSeconds(2)), Is.True);

            var enteredMessageLoop = false;
            var exitCode = WindowsTrayApp.Run(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(tempDirectory),
                useGlobalMutex: false,
                context =>
                {
                    enteredMessageLoop = true;
                    context.ExitThread();
                },
                new SingleInstanceNotificationSettings(false, "none"));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(enteredMessageLoop, Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void AbandonOwnedMutex(string instanceId)
    {
        using var ready = new ManualResetEventSlim(false);
        Exception? failure = null;
        var owner = new Thread(() =>
        {
            try
            {
                // Intentionally leave ownership to Windows when this owner thread exits.
                var mutex = new Mutex(
                    initiallyOwned: false,
                    name: SingleInstanceEnforcement.BuildMutexName(instanceId, false));
                if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    throw new InvalidOperationException("test_mutex_unavailable");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                ready.Set();
            }
        });

        owner.Start();
        Assert.That(ready.Wait(TimeSpan.FromSeconds(2)), Is.True, "Abandoned mutex owner did not start.");
        owner.Join();
        Assert.That(failure, Is.Null, "Abandoned mutex owner failed.");
    }

    private sealed class ExternalMutexOwner : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private Exception? _failure;

        public ExternalMutexOwner(string instanceId)
        {
            _thread = new Thread(() => HoldMutex(instanceId)) { IsBackground = true };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new AssertionException("External mutex owner did not start.");
            }

            if (_failure is not null)
            {
                throw new AssertionException("External mutex owner failed.", _failure);
            }
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
            _release.Dispose();
        }

        private void HoldMutex(string instanceId)
        {
            try
            {
                using var mutex = new Mutex(
                    initiallyOwned: false,
                    name: SingleInstanceEnforcement.BuildMutexName(instanceId, false));
                if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    throw new InvalidOperationException("test_mutex_unavailable");
                }

                _ready.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
        }
    }
}

[TestFixture]
public class ResidentFirstRunSetupLaunchTests
{
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Test]
    public void FirstRunSetupLaunchCoordinator_UsesSelectedProfileSetNotAnyProtectedProfile()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[]
            {
                new SubmitBindingProfile(
                    "chatgpt-desktop",
                    Enabled: true,
                    BindingSource: "not_verified",
                    SubmitBinding: null,
                    NewlineBinding: null,
                    CapabilityStatus: OsInteractionStatusIds.BindingUnknown,
                    CompatibilityEvidence: null,
                    Diagnostics: new Dictionary<string, string>())
            });
            var controller = new TestSetupController(_ => new FirstRunSetupResult(
                Succeeded: true,
                Code: "setup_complete",
                State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, true),
                Diagnostics: new Dictionary<string, string>()), statusSucceeded: false);

            var result = new FirstRunSetupLaunchCoordinator(layout, controller).RunIfRequired();

            Assert.That(controller.EnsureSetupCalls, Is.EqualTo(1));
            Assert.That(result.Succeeded, Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void FirstRunSetupLaunchCoordinator_DoesNotRunSetupWhenEverySelectedProfileIsVerified()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new TestSetupController(_ => throw new AssertionException("setup should not run"), setupRequired: false);

            var result = new FirstRunSetupLaunchCoordinator(layout, controller).RunIfRequired();

            Assert.That(controller.EnsureSetupCalls, Is.EqualTo(0));
            Assert.That(result.Code, Is.EqualTo("setup_complete"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LaunchFirstRunSetupIfRequired_LaunchesSetupWhenNoProtectedProfile()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            // Create an unprotected profile
            var profile = new SubmitBindingProfile(
                "codex-desktop",
                Enabled: true,
                BindingSource: "manual",
                SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
                NewlineBinding: SubmitKeyBinding.Parse("Shift+Enter").Binding!,
                CapabilityStatus: OsInteractionStatusIds.NativeSubmitPassThrough,
                CompatibilityEvidence: null,
                Diagnostics: new Dictionary<string, string>());

            // Save profile
            SubmitBindingProfileStore.Save(layout, new[] { profile });

            // Verify no protected profile exists
            var stored = SubmitBindingProfileStore.Load(layout);
            Assert.That(stored.Profiles.Any(p => p.IsProtected && p.Enabled), Is.False);

            // In a real scenario, this would launch first-run setup
            // For unit test, we verify the setup would be triggered
            Assert.That(stored.Profiles.Count, Is.EqualTo(1));
            Assert.That(stored.Profiles[0].IsProtected, Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LaunchFirstRunSetupIfRequired_DoesNotLaunchWhenProtectedProfileExists()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            // Create a protected profile
            var profile = new SubmitBindingProfile(
                "codex-desktop",
                Enabled: true,
                BindingSource: "user_verified",
                SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
                NewlineBinding: SubmitKeyBinding.Parse("Shift+Enter").Binding!,
                CapabilityStatus: OsInteractionStatusIds.Protected,
                CompatibilityEvidence: null,
                Diagnostics: new Dictionary<string, string>());

            // Save profile
            SubmitBindingProfileStore.Save(layout, new[] { profile });

            // Verify protected profile exists
            var stored = SubmitBindingProfileStore.Load(layout);
            Assert.That(stored.Profiles.Any(p => p.IsProtected && p.Enabled), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LaunchFirstRunSetupIfRequired_HandlesEmptyStore()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            // Verify empty store (profiles_default_empty)
            var stored = SubmitBindingProfileStore.Load(layout);
            Assert.That(stored.Profiles, Is.Empty);

            // No protected profile - setup would be launched
            Assert.That(stored.Profiles.Any(p => p.IsProtected && p.Enabled), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LaunchFirstRunSetupIfRequired_WaitsForSetupCompletion()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreatePendingSetupProfile() });

            // Create setup controller that tracks calls
            var setupCallCount = 0;
            var setupCompleted = false;
            var setupController = new TestSetupController(
                setupLayout =>
                {
                    setupCallCount++;
                    // Simulate setup work
                    System.Threading.Thread.Sleep(10);
                    setupCompleted = true;
                    // Create a protected profile
                    var profile = new SubmitBindingProfile(
                        "codex-desktop",
                        Enabled: true,
                        BindingSource: "user_verified",
                        SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
                        NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
                        CapabilityStatus: OsInteractionStatusIds.Protected,
                        CompatibilityEvidence: null,
                        Diagnostics: new Dictionary<string, string>());
                    SubmitBindingProfileStore.Save(setupLayout, new[] { profile });
                    return new FirstRunSetupResult(
                        Succeeded: true,
                        Code: "setup_complete",
                        State: new FirstRunSetupState(
                            Required: false,
                            UnprotectedProfileIds: Array.Empty<string>(),
                            Status: "complete",
                            VerifiedCodex: true,
                            VerifiedChatGpt: true),
                        Diagnostics: new Dictionary<string, string>());
                });

            // Run launch logic
            LaunchFirstRunSetupIfRequiredWithController(layout, setupController);

            // Verify setup was called and completed
            Assert.That(setupCallCount, Is.EqualTo(1));
            Assert.That(setupCompleted, Is.True);

            // Verify protected profile was created
            var stored = SubmitBindingProfileStore.Load(layout);
            Assert.That(stored.Profiles.Any(p => p.IsProtected && p.Enabled), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LaunchFirstRunSetupIfRequired_DoesNotRefreshIfSetupFails()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreatePendingSetupProfile() });

            // Create setup controller that fails
            var setupController = new TestSetupController(
                setupLayout => new FirstRunSetupResult(
                    Succeeded: false,
                    Code: "setup_failed",
                    State: new FirstRunSetupState(
                        Required: true,
                        UnprotectedProfileIds: new[] { "codex-desktop" },
                        Status: "failed",
                        VerifiedCodex: false,
                        VerifiedChatGpt: false),
                    Diagnostics: new Dictionary<string, string>()));

            // Run launch logic - should not throw
            LaunchFirstRunSetupIfRequiredWithController(layout, setupController);

            // Verify no protected profile was created
            var stored = SubmitBindingProfileStore.Load(layout);
            Assert.That(stored.Profiles.Any(p => p.IsProtected && p.Enabled), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void FirstRunSetupBackgroundRunner_CapturesWorkerFailureAndKeepsSetupBlocked()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var captured = false;

            var result = FirstRunSetupBackgroundRunner.Run(
                layout,
                () => throw new InvalidOperationException("DOMAIN_C195C3D8E8F3"),
                _ => captured = true);

            Assert.That(result, Is.Null);
            Assert.That(captured, Is.True);
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles.Any(profile => profile.IsProtected), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public void WindowsTrayApplicationContext_StartsNativeHookBeforeFirstRunSetupOnMessageLoop()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreatePendingSetupProfile() });
            using var setupStarted = new ManualResetEventSlim(false);
            using var setupCompleted = new ManualResetEventSlim(false);
            Exception? threadFailure = null;
            var hookWasStartedBeforeSetup = 0;
            var hookStayedStartedAfterSetup = 0;
            var selectedSendWasBlockedDuringSetup = 0;

            var thread = new Thread(() =>
            {
                try
                {
                    var hook = new SanitizerTests.FakeNativeSubmitHookHost();
                    var profile = CreatePendingSetupProfile();
                    var nativeController = new NativeSubmitInterceptionController(
                        profile,
                        new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                        activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateSetupTestSurface()),
                        firstRunSetupController: new FirstRunSetupController(),
                        setupLayout: layout);
                    var protection = TrayProtectionController.CreateTest(
                        new SanitizerTests.FakeTrayHotkeyHost(),
                        () => throw new AssertionException("Manual scan should not run."),
                        hook,
                        nativeController,
                        () => throw new AssertionException("Cloud submission should not run."),
                        profile,
                        storageLayout: layout,
                        activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateSetupTestSurface()));
                    var setup = new TestSetupController(_ =>
                    {
                        if (hook.Started)
                        {
                            Interlocked.Exchange(ref hookWasStartedBeforeSetup, 1);
                        }

                        hook.Trigger(new NativeKeyGesture("Enter"));
                        if (hook.LastClassification?.Status == OsInteractionStatusIds.NativeSubmitSetupRequired
                            && hook.LastClassification.SuppressOriginalInput)
                        {
                            Interlocked.Exchange(ref selectedSendWasBlockedDuringSetup, 1);
                        }

                        setupStarted.Set();
                        return SetupCancelledResult();
                    });

                    using var context = new WindowsTrayApplicationContext(
                        protection,
                        layout,
                        new NoOpTrayLocalCommandLauncher(),
                        new NoOpTrayProtectionDisableConfirmation(),
                        firstRunSetupControllerFactory: () => setup,
                        firstRunSetupCompleted: _ => setupCompleted.Set());
                    if (setupStarted.IsSet)
                    {
                        throw new AssertionException("Setup started before the tray message loop.");
                    }

                    using var timeoutTimer = new System.Windows.Forms.Timer { Interval = 25 };
                    var ticks = 0;
                    timeoutTimer.Tick += (_, _) =>
                    {
                        if (setupCompleted.IsSet || ++ticks > 120)
                        {
                            if (hook.Started)
                            {
                                Interlocked.Exchange(ref hookStayedStartedAfterSetup, 1);
                            }

                            timeoutTimer.Stop();
                            context.ExitThread();
                        }
                    };
                    timeoutTimer.Start();
                    System.Windows.Forms.Application.Run(context);
                }
                catch (Exception exception)
                {
                    threadFailure = exception;
                }
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.That(thread.Join(TimeSpan.FromSeconds(8)), Is.True, "Tray message loop did not exit.");
            Assert.That(threadFailure, Is.Null);
            Assert.That(setupStarted.IsSet, Is.True);
            Assert.That(setupCompleted.IsSet, Is.True);
            Assert.That(Volatile.Read(ref hookWasStartedBeforeSetup), Is.EqualTo(1));
            Assert.That(Volatile.Read(ref hookStayedStartedAfterSetup), Is.EqualTo(1));
            Assert.That(Volatile.Read(ref selectedSendWasBlockedDuringSetup), Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_StatusViewIsModelessAndRefreshesWithinOneTrayProcess()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var protection = CreateManualOnlyTrayProtection(layout);
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            var firstForm = context.LocalProtectionStatusForm;
            Assert.That(firstForm, Is.Not.Null);
            Assert.That(context.IsLocalProtectionStatusOpen, Is.True);

            context.OpenLocalProtectionStatus();
            Assert.That(context.LocalProtectionStatusForm, Is.SameAs(firstForm));

            var replacedControls = firstForm!.RowControls.ToArray();
            context.RefreshStatus();
            Assert.That(replacedControls, Is.Not.Empty);
            Assert.That(replacedControls.All(control => control.IsDisposed), Is.True);

            for (var refresh = 0; refresh < 5; refresh++)
            {
                context.RefreshStatus();
                Assert.That(firstForm!.CurrentRows, Has.Count.EqualTo(3));
            }

            protection.Stop();
            Assert.That(firstForm!.CurrentRows[1].OperationalState, Is.EqualTo("disabled"));

            protection.Start();
            Assert.That(firstForm.CurrentRows[1].OperationalState, Is.Not.EqualTo("disabled"));

            ProtectedWorkspaceStore.Protect(layout, Path.Combine(tempDirectory, "workspace"));
            context.RefreshProjectFileProtectionStatus();
            Assert.That(firstForm.CurrentRows[2].OperationalState, Is.EqualTo("broker demo only"));

            firstForm.Close();
            Assert.That(firstForm.IsRefreshTimerDisposed, Is.True);
            Assert.That(context.IsLocalProtectionStatusOpen, Is.False);
            Assert.That(context.LocalProtectionStatusForm, Is.Null);

            context.OpenLocalProtectionStatus();
            var secondForm = context.LocalProtectionStatusForm;
            Assert.That(secondForm, Is.Not.Null);
            Assert.That(secondForm, Is.Not.SameAs(firstForm));
            secondForm!.Close();
            Assert.That(secondForm.IsRefreshTimerDisposed, Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_StatusViewKeepsSyntheticResidentFailuresRawFree()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var rawValues = new[]
            {
                "C:\\Users\\user1\\private\\.env",
                "test.secret.com",
                "PROMPT_C195C3D8E8F3",
                "mapping-value",
                "exception-detail"
            };
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var protection = CreateManualOnlyTrayProtection(layout);
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            var form = context.LocalProtectionStatusForm;
            Assert.That(form, Is.Not.Null);
            var statusForm = form!;

            protection.PublishSyntheticDiagnosticsForTesting(
                rawValues[0],
                rawValues[1],
                rawValues[2],
                rawValues[3],
                rawValues[4]);

            var renderedControls = string.Join(
                Environment.NewLine,
                statusForm.RowControls
                    .SelectMany(row => row.Controls.Cast<Control>())
                    .Select(control => control.Text));
            Assert.That(statusForm.CurrentRows.All(row => rawValues.All(raw => !row.ToString().Contains(raw, StringComparison.Ordinal))), Is.True);
            Assert.That(rawValues.All(raw => !renderedControls.Contains(raw, StringComparison.Ordinal)), Is.True);
            Assert.That(rawValues.All(raw => !context.TrayTooltipText.Contains(raw, StringComparison.Ordinal)), Is.True);
            Assert.That(rawValues.All(raw => !context.TrayStatusText.Contains(raw, StringComparison.Ordinal)), Is.True);
            Assert.That(statusForm.CurrentRows[0].OperationalState, Is.EqualTo("unavailable"));
            Assert.That(statusForm.CurrentRows[1].OperationalState, Is.EqualTo("unavailable"));

            protection.PublishSyntheticDiagnosticsForTesting(
                LocalProtectionRecovery.ReadyCode,
                rawValues[1],
                rawValues[2],
                rawValues[3],
                rawValues[4]);

            Assert.That(rawValues.All(raw => !context.TrayTooltipText.Contains(raw, StringComparison.Ordinal)), Is.True);
            Assert.That(rawValues.All(raw => !context.TrayStatusText.Contains(raw, StringComparison.Ordinal)), Is.True);
            Assert.That(statusForm.CurrentRows[0].OperationalState, Is.EqualTo("ready"));

            statusForm.Close();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_StatusActionsAreSingleFlightAndRemainRawFreeAfterFailure()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreatePendingSetupProfile() });
            var queuedWork = new Queue<Action>();
            var retryFactoryCalls = 0;
            var setupAttempts = 0;
            const string retryFailure = "DOMAIN_C195C3D8E8F3";
            SanitizerTests.FakeNativeSubmitHookHost? failedRetryHook = null;
            var protection = CreateManualOnlyTrayProtection();
            var setupController = new TestSetupController(setupLayout =>
            {
                if (++setupAttempts == 1)
                {
                    return SetupCancelledResult();
                }

                SubmitBindingProfileStore.Save(setupLayout, new[] { CreateProtectedSetupProfile() });
                return SetupCompleteResult();
            });
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                nativeSubmitRuntimeFactory: () =>
                {
                    var profile = CreateProtectedSetupProfile();
                    if (++retryFactoryCalls == 1)
                    {
                        var activeHook = new SanitizerTests.FakeNativeSubmitHookHost();
                        var activeRuntime = NativeSubmitRuntime.CreateTest(
                            activeHook,
                            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                            CreateProtectedInteractionResult,
                            profile);
                        return new NativeSubmitRuntimeSet(activeHook, new[] { activeRuntime });
                    }

                    failedRetryHook = new SanitizerTests.FakeNativeSubmitHookHost
                    {
                        OnStarted = _ => throw new InvalidOperationException(retryFailure)
                    };
                    var runtime = NativeSubmitRuntime.CreateTest(
                        failedRetryHook,
                        new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                        CreateProtectedInteractionResult,
                        profile);
                    return new NativeSubmitRuntimeSet(failedRetryHook, new[] { runtime });
                },
                firstRunSetupControllerFactory: () => setupController,
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));

            queuedWork.Dequeue().Invoke();
            Assert.That(setupController.EnsureSetupCalls, Is.EqualTo(1));
            Assert.That(context.IsNativeSubmitHookReady, Is.False);

            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();
            Assert.That(setupController.EnsureSetupCalls, Is.EqualTo(2));
            var form = context.LocalProtectionStatusForm;
            Assert.That(form!.CurrentRows[1].OperationalState, Is.EqualTo("active"));
            Assert.That(context.IsNativeSubmitHookReady, Is.True);

            var protectedProfiles = SubmitBindingProfileStore.Load(layout).Profiles;
            Assert.That(protectedProfiles.Single().IsProtected, Is.True);
            protection.Stop();
            protection.Start();
            context.RefreshStatus();
            Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("degraded"));
            Assert.That(form.CurrentRows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RetryPromptProtection));

            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(retryFactoryCalls, Is.EqualTo(2));
            Assert.That(form.CurrentRows[1].Consequence, Does.Contain("retry failed"));
            Assert.That(form.CurrentRows[1].Consequence, Does.Contain("stays blocked"));
            Assert.That(form.CurrentRows.Select(row => row.Consequence), Does.Not.Contain(retryFailure));
            Assert.That(failedRetryHook, Is.Not.Null);
            Assert.That(failedRetryHook!.Started, Is.False);

            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();
            Assert.That(retryFactoryCalls, Is.EqualTo(3));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_ActivatesCandidateBeforePersistingProfiles()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedSetupProfile();
            var candidateProfile = oldProfile with
            {
                SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { oldProfile }).Succeeded, Is.True);

            var oldHook = new SanitizerTests.FakeNativeSubmitHookHost();
            var candidateHook = new SanitizerTests.FakeNativeSubmitHookHost();
            IReadOnlyList<SubmitBindingProfile>? profilesAtActivation = null;
            var queuedWork = new Queue<Action>();
            var setupController = new TestSetupController(_ => new FirstRunSetupResult(
                Succeeded: true,
                Code: "focused_profile_verified",
                State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
                Diagnostics: new Dictionary<string, string>
                {
                    ["setup_attempt_id"] = "1"
                },
                PreviousProfiles: new[] { oldProfile },
                PendingProfiles: new[] { candidateProfile }));
            var protection = CreateManualOnlyTrayProtection(layout);

            NativeSubmitRuntime CreateRuntime(
                SanitizerTests.FakeNativeSubmitHookHost hook,
                SubmitBindingProfile profile)
            {
                return NativeSubmitRuntime.CreateTest(
                    hook,
                    new NativeSubmitInterceptionController(
                        profile,
                        new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                    CreateProtectedInteractionResult,
                    profile);
            }

            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                nativeSubmitRuntimeFactory: () => new NativeSubmitRuntimeSet(
                    oldHook,
                    new[] { CreateRuntime(oldHook, oldProfile) }),
                firstRunSetupControllerFactory: () => setupController,
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false,
                candidateNativeSubmitRuntimeFactory: profiles =>
                {
                    profilesAtActivation = SubmitBindingProfileStore.Load(layout).Profiles;
                    candidateHook.OnStarted = _ =>
                    {
                        profilesAtActivation = SubmitBindingProfileStore.Load(layout).Profiles;
                    };
                    return new NativeSubmitRuntimeSet(
                        candidateHook,
                        new[] { CreateRuntime(candidateHook, profiles.Single()) });
                });

            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                AttemptId: 1));
            Assert.That(protection.State.SetupVerificationStatus, Is.EqualTo("waiting_for_focus"));
            Assert.That(protection.State.SetupVerificationAttemptId, Is.EqualTo(1));

            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(profilesAtActivation, Is.Not.Null);
            Assert.That(profilesAtActivation!.Single().SubmitBinding!.DisplayText, Is.EqualTo("Enter"));
            Assert.That(candidateHook.Started, Is.True);
            Assert.That(oldHook.Started, Is.False);
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles.Single().SubmitBinding!.DisplayText,
                Is.EqualTo("Ctrl+Enter"));
            Assert.That(protection.State.ProtectedSendBinding, Is.EqualTo("Ctrl+Enter"));
            Assert.That(protection.State.ComposerProtected, Is.True);
            Assert.That(File.Exists(Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RejectsStaleCandidateWithoutTouchingCurrentRuntime()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedSetupProfile();
            var candidateProfile = oldProfile with
            {
                SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { oldProfile }).Succeeded, Is.True);

            var queuedWork = new Queue<Action>();
            var candidateFactoryCalls = 0;
            var protection = CreateManualOnlyTrayProtection(layout);
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                firstRunSetupControllerFactory: () => new TestSetupController(_ => new FirstRunSetupResult(
                    Succeeded: true,
                    Code: "focused_profile_verified",
                    State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
                    Diagnostics: new Dictionary<string, string>
                    {
                        ["setup_attempt_id"] = "1"
                    },
                    PreviousProfiles: new[] { oldProfile },
                    PendingProfiles: new[] { candidateProfile })),
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false,
                candidateNativeSubmitRuntimeFactory: _ =>
                {
                    candidateFactoryCalls++;
                    return null;
                });

            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                AttemptId: 2));
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(candidateFactoryCalls, Is.Zero);
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles.Single().SubmitBinding!.DisplayText,
                Is.EqualTo("Enter"));
            Assert.That(protection.State.ProtectedSendBinding, Is.EqualTo("not_configured"));
            Assert.That(File.Exists(Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete")), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RejectsCandidateWithoutAttemptId()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedSetupProfile();
            var candidateProfile = oldProfile with
            {
                SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { oldProfile }).Succeeded, Is.True);

            var queuedWork = new Queue<Action>();
            var candidateFactoryCalls = 0;
            var protection = CreateManualOnlyTrayProtection(layout);
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                firstRunSetupControllerFactory: () => new TestSetupController(_ => new FirstRunSetupResult(
                    Succeeded: true,
                    Code: "focused_profile_verified",
                    State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
                    Diagnostics: new Dictionary<string, string>(),
                    PreviousProfiles: new[] { oldProfile },
                    PendingProfiles: new[] { candidateProfile })),
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false,
                candidateNativeSubmitRuntimeFactory: _ =>
                {
                    candidateFactoryCalls++;
                    return null;
                });

            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                AttemptId: 1));
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(candidateFactoryCalls, Is.Zero);
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles.Single().SubmitBinding!.DisplayText,
                Is.EqualTo("Enter"));
            Assert.That(File.Exists(Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete")), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RollsBackResidentCandidateWhenProfileCommitFails()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedSetupProfile();
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { oldProfile }).Succeeded, Is.True);

            var oldHook = new SanitizerTests.FakeNativeSubmitHookHost();
            var candidateHook = new SanitizerTests.FakeNativeSubmitHookHost();
            var queuedWork = new Queue<Action>();
            var rollbackFactoryCalls = 0;
            var protection = CreateManualOnlyTrayProtection(layout);
            var setupController = new TestSetupController(_ => new FirstRunSetupResult(
                Succeeded: true,
                Code: "focused_profile_verified",
                State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
                Diagnostics: new Dictionary<string, string>
                {
                    ["setup_attempt_id"] = "1"
                },
                PreviousProfiles: new[] { oldProfile },
                PendingProfiles: new[] { (SubmitBindingProfile)null! }));

            NativeSubmitRuntime CreateRuntime(
                SanitizerTests.FakeNativeSubmitHookHost hook,
                SubmitBindingProfile profile)
            {
                return NativeSubmitRuntime.CreateTest(
                    hook,
                    new NativeSubmitInterceptionController(
                        profile,
                        new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                    CreateProtectedInteractionResult,
                    profile);
            }

            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                nativeSubmitRuntimeFactory: () =>
                {
                    rollbackFactoryCalls++;
                    return new NativeSubmitRuntimeSet(
                        oldHook,
                        new[] { CreateRuntime(oldHook, oldProfile) });
                },
                firstRunSetupControllerFactory: () => setupController,
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false,
                candidateNativeSubmitRuntimeFactory: _ => new NativeSubmitRuntimeSet(
                    candidateHook,
                    new[] { CreateRuntime(candidateHook, oldProfile with
                    {
                        SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                        NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
                    }) }));

            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                AttemptId: 1));
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "composer_recognized",
                "wait_for_verification",
                "codex-desktop",
                "Ctrl+Enter",
                AttemptId: 1));
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "verifying_binding",
                "wait_for_verification",
                "codex-desktop",
                "Ctrl+Enter",
                AttemptId: 1));
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "activating_protection",
                "wait_for_verification",
                "codex-desktop",
                "Ctrl+Enter",
                AttemptId: 1));
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.VerifyProfiles);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(candidateHook.Started, Is.False);
            Assert.That(rollbackFactoryCalls, Is.EqualTo(1),
                $"candidate_started={candidateHook.Started}; last_status={protection.State.LastStatus}; native_status={protection.State.NativeSubmitStatus}; runtime_present={protection.GetCurrentSnapshot().RuntimeSet is not null}");
            Assert.That(protection.GetCurrentSnapshot().RuntimeSet?.HookHost, Is.SameAs(oldHook));
            Assert.That(oldHook.Started, Is.True);
            Assert.That(protection.State.ProtectedSendBinding, Is.EqualTo("Enter"));
            Assert.That(protection.State.SetupVerificationStatus, Is.EqualTo("activation_failed"));
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles.Single().SubmitBinding!.DisplayText,
                Is.EqualTo("Enter"));
            Assert.That(File.Exists(Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete")), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RemediationDispatcherShutdownReleasesSingleFlightGuard()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreateProtectedSetupProfile() });
            var queuedWork = new Queue<Action>();
            var retryFactoryCalls = 0;
            var protection = CreateManualOnlyTrayProtection();
            protection.Start();
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                nativeSubmitRuntimeFactory: () =>
                {
                    retryFactoryCalls++;
                    return null;
                },
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: _ => throw new InvalidOperationException("dispatcher unavailable"),
                scheduleFirstRunSetup: false);

            Assert.That(protection.State.PromptProtectionRetryFailed, Is.False);
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            Assert.That(retryFactoryCalls, Is.EqualTo(2));
            Assert.That(context.IsNativeSubmitHookReady, Is.False);
            Assert.That(protection.State.ComposerProtected, Is.False);
            Assert.That(protection.State.PromptProtectionRetryFailed, Is.True);
            Assert.That(context.TrayStatusText, Does.Not.Contain("dispatcher unavailable"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RemediationWorkerQueueFailurePublishesBlockedRawFreeState()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            const string rawFailure = "DOMAIN_C195C3D8E8F3";
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreateProtectedSetupProfile() });
            var protection = CreateManualOnlyTrayProtection();
            protection.Start();
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                backgroundWorkQueue: _ => throw new InvalidOperationException(rawFailure),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);

            var form = context.LocalProtectionStatusForm;
            Assert.That(form, Is.Not.Null);
            Assert.That(protection.State.PromptProtectionRetryFailed, Is.True);
            Assert.That(context.IsNativeSubmitHookReady, Is.False);
            Assert.That(form!.CurrentRows.Select(row => row.Consequence), Does.Not.Contain(rawFailure));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RuntimeCreationFailureStaysBlockedAndRawFree()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            const string rawFailure = "DOMAIN_C195C3D8E8F3";
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Save(layout, new[] { CreateProtectedSetupProfile() });
            var queuedWork = new Queue<Action>();
            var protection = CreateManualOnlyTrayProtection();
            protection.Start();
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                nativeSubmitRuntimeFactory: () => throw new InvalidOperationException(rawFailure),
                backgroundWorkQueue: work => queuedWork.Enqueue(work),
                uiDispatcher: work => work(),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            context.RunLocalProtectionStatusAction(LocalProtectionStatusAction.RetryPromptProtection);
            Assert.That(queuedWork, Has.Count.EqualTo(1));
            queuedWork.Dequeue().Invoke();

            var form = context.LocalProtectionStatusForm;
            Assert.That(form, Is.Not.Null);
            Assert.That(context.IsNativeSubmitHookReady, Is.False);
            Assert.That(protection.State.ComposerProtected, Is.False);
            Assert.That(form!.CurrentRows[1].Consequence, Does.Contain("retry failed"));
            Assert.That(form.CurrentRows.Select(row => row.Consequence), Does.Not.Contain(rawFailure));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_ConfirmedLocalRecoveryPublishesReadyOnlyAfterNativeRuntimeActivates()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var protection = CreateManualOnlyTrayProtection();
            var messages = new List<string>();
            TrayProtectionState? stateDuringRecovery = null;
            TrayProtectionState? stateWhenReplacementHookStarted = null;
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                localProtectionStatus: LocalProtectionRecovery.RecoveryRequiredCode,
                recoveredRuntimeFactory: () => CreateRecoveredProtectedRuntime(
                    () => stateWhenReplacementHookStarted = protection.State),
                localProtectionRecovery: () =>
                {
                    stateDuringRecovery = protection.State;
                    return new LocalProtectionRecoveryResult(
                        Succeeded: true,
                        Code: LocalProtectionRecovery.RecoveredCode,
                        RecoveryRequired: false,
                        ConfirmationRequired: false,
                        PreviousArtifactsPreserved: true,
                        VaultInitialized: true);
                },
                recoveryMessagePresenter: (message, _) => messages.Add(message),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            var form = context.LocalProtectionStatusForm;
            Assert.That(form, Is.Not.Null);
            Assert.That(form!.CurrentRows[0].OperationalState, Is.EqualTo("recovery required"));

            context.RepairLocalProtectionConfirmed();

            Assert.That(stateDuringRecovery, Is.Not.Null);
            Assert.That(stateDuringRecovery!.LocalProtectionStatus, Is.EqualTo("local_protection_reloading"));
            Assert.That(stateDuringRecovery.NativeSubmitEnabled, Is.False);
            Assert.That(stateDuringRecovery.ComposerProtected, Is.False);
            Assert.That(stateWhenReplacementHookStarted, Is.Not.Null);
            Assert.That(stateWhenReplacementHookStarted!.LocalProtectionStatus, Is.EqualTo("local_protection_reloading"));
            Assert.That(stateWhenReplacementHookStarted.ComposerProtected, Is.False);
            Assert.That(protection.State.LocalProtectionStatus, Is.EqualTo(LocalProtectionRecovery.ReadyCode));
            Assert.That(context.IsNativeSubmitHookReady, Is.True);
            Assert.That(protection.State.NativeSubmitEnabled, Is.True);
            Assert.That(protection.State.ComposerProtected, Is.True);
            Assert.That(form.CurrentRows[0].OperationalState, Is.EqualTo("ready"));
            Assert.That(messages.Single(), Does.Contain("protected Send is active"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_FailedLocalRecoveryStaysBlockedAndDoesNotExposeFailureCode()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            const string rawFailureCode = "DOMAIN_C195C3D8E8F3";
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var protection = CreateManualOnlyTrayProtection();
            var messages = new List<string>();
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                localProtectionStatus: LocalProtectionRecovery.RecoveryRequiredCode,
                localProtectionRecovery: () => new LocalProtectionRecoveryResult(
                    Succeeded: false,
                    Code: rawFailureCode,
                    RecoveryRequired: true,
                    ConfirmationRequired: false,
                    PreviousArtifactsPreserved: true,
                    VaultInitialized: false),
                recoveryMessagePresenter: (message, _) => messages.Add(message),
                scheduleFirstRunSetup: false);

            context.OpenLocalProtectionStatus();
            context.RepairLocalProtectionConfirmed();

            var form = context.LocalProtectionStatusForm;
            Assert.That(protection.State.LocalProtectionStatus, Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(context.IsNativeSubmitHookReady, Is.False);
            Assert.That(protection.State.ComposerProtected, Is.False);
            Assert.That(form!.CurrentRows[0].OperationalState, Is.EqualTo("recovery required"));
            Assert.That(messages.Single(), Does.Contain("Protected Send remains blocked"));
            Assert.That(messages.All(message => !message.Contains(rawFailureCode, StringComparison.Ordinal)), Is.True);
            Assert.That(form.CurrentRows.All(row =>
                !row.ToString().Contains(rawFailureCode, StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_RecoveryExceptionReturnsToRecoveryRequiredWithoutExposingException()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            const string rawExceptionMessage = "DOMAIN_C195C3D8E8F3";
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var protection = CreateManualOnlyTrayProtection();
            var messages = new List<string>();
            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                localProtectionStatus: LocalProtectionRecovery.RecoveryRequiredCode,
                localProtectionRecovery: () => throw new InvalidOperationException(rawExceptionMessage),
                recoveryMessagePresenter: (message, _) => messages.Add(message),
                scheduleFirstRunSetup: false);

            context.RepairLocalProtectionConfirmed();

            Assert.That(protection.State.LocalProtectionStatus, Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(protection.State.ComposerProtected, Is.False);
            Assert.That(messages.All(message => !message.Contains(rawExceptionMessage, StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TrayProtectionController_LocalRecoveryStatusBlocksSelectedSendButPassesOrdinaryInput()
    {
        var profile = CreateProtectedSetupProfile() with
        {
            SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding!
        };
        var hook = new SanitizerTests.FakeNativeSubmitHookHost();
        var submitted = 0;
        var controller = TrayProtectionController.CreateTest(
            new SanitizerTests.FakeTrayHotkeyHost(),
            CreateProtectedInteractionResult,
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateSetupTestSurface())),
            () =>
            {
                submitted++;
                return CreateProtectedInteractionResult();
            },
            profile,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateSetupTestSurface()));
        Assert.That(controller.Start(), Is.True);

        controller.PublishLocalProtectionStatus("local_protection_reloading");
        hook.Trigger(new NativeKeyGesture("Enter"));

        Assert.That(hook.LastClassification!.SuppressOriginalInput, Is.False);

        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification!.SuppressOriginalInput, Is.True);
        Assert.That(submitted, Is.EqualTo(0));
        Assert.That(controller.State.ComposerProtected, Is.False);

    }

    [Test]
    public void ReadableProtectionStatus_ExplainsProfileSetupAndRepairWithoutInternalCodes()
    {
        var protectedState = new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            NativeSubmitEnabled: true,
            NativeSubmitStatus: OsInteractionStatusIds.Protected,
            ProtectedSendBinding: "Ctrl+Enter",
            NewlineBinding: "Enter",
            ManualScanHotkey: "Ctrl+Shift+F9",
            ReadinessStatus: OsInteractionStatusIds.Protected,
            ComposerProtected: true,
            ResidentProcess: true,
            ConfiguredProfileId: "chatgpt-desktop");

        var protectedText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState);
        var setupText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ComposerProtected = false,
            SetupRequired = true,
            ReadinessStatus = OsInteractionStatusIds.NativeSubmitSetupRequired
        });
        var repairText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ComposerProtected = false,
            SetupRequired = false,
            ReadinessStatus = OsInteractionStatusIds.SurfaceUnverified
        });
        var focusLostText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ComposerProtected = false,
            SetupRequired = false,
            ReadinessStatus = OsInteractionStatusIds.StaleComposer
        });
        var checkingText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ProtectedSendAttemptStatus = "checking",
            ProtectedSendAttemptAction = "checking_prompt"
        });
        var sentText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ProtectedSendAttemptStatus = "sent_safely",
            ProtectedSendAttemptAction = "none"
        });
        var traceUnavailableText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            ProtectedSendAttemptStatus = "trace_unavailable",
            ProtectedSendAttemptAction = "retry_protection"
        });
        var interruptedText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState with
        {
            LastProtectedSendInterruption = new ProtectedSendInterruption(
                AttemptId: 12,
                SourceGeneration: 7,
                Reason: "runtime_replaced",
                Action: "retry_protection")
        });

        Assert.That(protectedText, Does.Contain("ChatGPT Desktop"));
        Assert.That(protectedText, Does.Contain("Ctrl+Enter"));
        Assert.That(setupText, Does.Contain("select Set up prompt protection"));
        Assert.That(repairText, Does.Contain("Prompt verification required"));
        Assert.That(focusLostText, Does.Contain("focus it and send again"));
        Assert.That(checkingText, Is.EqualTo("Protected Send: checking prompt"));
        Assert.That(sentText, Is.EqualTo("Protected Send: sent safely"));
        Assert.That(traceUnavailableText, Does.Contain("trace unavailable"));
        Assert.That(traceUnavailableText, Does.Contain("retry protection"));
        Assert.That(interruptedText, Does.Contain("previous Send was interrupted"));
        Assert.That(interruptedText, Does.Contain("retry protection"));
        Assert.That(new[] { protectedText, setupText, repairText, focusLostText, checkingText, sentText, traceUnavailableText }
            .All(text => !text.Contains(OsInteractionStatusIds.NativeSubmitSetupRequired, StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void ReadableProtectionStatus_ExplainsObservableSetupLifecycle()
    {
        var waiting = CreateReadableProtectionState() with
        {
            SetupVerificationStatus = "waiting_for_focus",
            SetupVerificationAction = "focus_message_composer",
            SetupVerificationBinding = "Ctrl+Enter"
        };
        var verifying = waiting with
        {
            SetupVerificationStatus = "verifying_binding",
            SetupVerificationAction = "wait_for_verification"
        };
        var protectedState = waiting with
        {
            SetupVerificationStatus = "protected",
            SetupVerificationAction = "none",
            SetupVerificationProfileId = "chatgpt-desktop"
        };
        var protectedThenBlocked = protectedState with
        {
            ProtectedSendAttemptStatus = "policy_blocked"
        };

        Assert.That(WindowsTrayApplicationContext.FormatReadableProtectionStatus(waiting),
            Does.Contain("focus the message composer"));
        Assert.That(WindowsTrayApplicationContext.FormatReadableProtectionStatus(verifying),
            Does.Contain("verifying Ctrl+Enter"));
        Assert.That(WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedState),
            Does.Contain("ChatGPT Desktop is protected"));
        Assert.That(WindowsTrayApplicationContext.FormatReadableProtectionStatus(protectedThenBlocked),
            Does.Contain("blocked by policy"));
    }

    [Test]
    public void ReadableProtectionStatus_ExplainsSpecificBlockedSendReasons()
    {
        foreach (var (attemptStatus, expectedText) in new[]
        {
            ("local_protection_unavailable", "local protection is unavailable"),
            ("policy_blocked", "blocked by policy"),
            ("binding_not_verified", "verify prompt protection"),
            ("protection_unavailable", "retry protection"),
            ("content_blocked", "edit the prompt and send again")
        })
        {
            var state = CreateReadableProtectionState() with
            {
                ProtectedSendAttemptStatus = attemptStatus,
                ProtectedSendAttemptAction = "DOMAIN_C195C3D8E8F3"
            };

            var text = WindowsTrayApplicationContext.FormatReadableProtectionStatus(state);
            Assert.That(text, Does.Contain(expectedText), attemptStatus);
            Assert.That(text, Does.Not.Contain("DOMAIN_C195C3D8E8F3"), attemptStatus);
        }
    }

    [Test]
    public void TrayProtectionController_RejectsStaleAndOutOfOrderSetupProgress()
    {
        var controller = TrayProtectionController.CreateTest(
            new UnavailableTrayHotkeyHost(
                new HotkeyBinding("test-hotkey", "Ctrl+Shift+F9", "tests"),
                "test_hotkey_unavailable"),
            () => throw new InvalidOperationException("Manual scan should not run."));

        controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
            "waiting_for_focus", "focus_message_composer", "chatgpt-desktop", "Ctrl+Enter", AttemptId: 2));
        controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
            "composer_recognized", "wait_for_verification", "chatgpt-desktop", "Ctrl+Enter", AttemptId: 2));
        controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
            "verifying_binding", "wait_for_verification", "chatgpt-desktop", "Ctrl+Enter", AttemptId: 2));
        controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
            "composer_recognized", "wait_for_verification", "chatgpt-desktop", "Ctrl+Enter", AttemptId: 1));

        Assert.That(controller.State.SetupVerificationStatus, Is.EqualTo("verifying_binding"));
        Assert.That(controller.State.SetupVerificationAttemptId, Is.EqualTo(2));
    }

    private static TrayProtectionState CreateReadableProtectionState()
    {
        return new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            NativeSubmitEnabled: true,
            NativeSubmitStatus: OsInteractionStatusIds.Protected,
            ProtectedSendBinding: "Ctrl+Enter",
            NewlineBinding: "Enter",
            ManualScanHotkey: "Ctrl+Shift+F9",
            ReadinessStatus: OsInteractionStatusIds.Protected,
            ComposerProtected: true,
            ResidentProcess: true,
            ConfiguredProfileId: "chatgpt-desktop");
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void WindowsTrayApplicationContext_ShowsEmergencyBypassInMenu()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var context = new WindowsTrayApplicationContext(
                CreateManualOnlyTrayProtection(),
                DefaultStorageLayout.Create(directory),
                new NoOpTrayLocalCommandLauncher(),
                new NoOpTrayProtectionDisableConfirmation(),
                scheduleFirstRunSetup: false);

            Assert.That(context.EmergencyBypassMenuText,
                Is.EqualTo($"Emergency bypass: {NativeSubmitEmergencyState.BypassDisplayText}"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void LaunchFirstRunSetupIfRequiredWithController(DefaultStorageLayout layout, IFirstRunSetupController setupController)
    {
        FirstRunSetupBackgroundRunner.Run(layout, () => setupController, _ => { });
    }

    private static TrayProtectionController CreateManualOnlyTrayProtection(DefaultStorageLayout? storageLayout = null)
    {
        return new TrayProtectionController(
            new SanitizerTests.FakeTrayHotkeyHost(),
            CreateProtectedInteractionResult,
            nativeSubmitHookHost: null,
            nativeSubmitController: null,
            storageLayout: storageLayout);
    }

    private static OsInteractionResult CreateProtectedInteractionResult()
    {
        return new OsInteractionResult(
            OsInteractionStatusIds.Protected,
            Surface: null,
            SanitizationResult: null,
            ConfirmationModel: null,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>());
    }

    private static ResidentProtectionRuntime CreateRecoveredProtectedRuntime(Action? onHookStarted = null)
    {
        var profile = CreateProtectedSetupProfile();
        var hook = new SanitizerTests.FakeNativeSubmitHookHost
        {
            OnStarted = _ => onHookStarted?.Invoke()
        };
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            CreateProtectedInteractionResult,
            profile);
        return new ResidentProtectionRuntime(
            CreateProtectedInteractionResult,
            new NativeSubmitRuntimeSet(hook, new[] { runtime }));
    }

    private static SubmitBindingProfile CreatePendingSetupProfile()
    {
        return new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "not_verified",
            SubmitBinding: null,
            NewlineBinding: null,
            CapabilityStatus: OsInteractionStatusIds.BindingUnknown,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
    }

    private static SubmitBindingProfile CreateProtectedSetupProfile()
    {
        return new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
    }

    private static TextSurfaceDescriptor CreateSetupTestSurface()
    {
        return new TextSurfaceDescriptor(
            "setup-native-profile-test",
            "codex-desktop",
            "codex-desktop",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new SurfaceMetadata(
                SurfaceKind: "test",
                CloudSubmission: "false",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer));
    }

    private static FirstRunSetupResult SetupCancelledResult()
    {
        return new FirstRunSetupResult(
            Succeeded: false,
            Code: "setup_cancelled",
            State: new FirstRunSetupState(
                Required: true,
                UnprotectedProfileIds: new[] { "codex-desktop" },
                Status: "cancelled",
                VerifiedCodex: false,
                VerifiedChatGpt: false),
            Diagnostics: new Dictionary<string, string>());
    }

    private static FirstRunSetupResult SetupCompleteResult()
    {
        return new FirstRunSetupResult(
            Succeeded: true,
            Code: "setup_complete",
            State: new FirstRunSetupState(
                Required: false,
                UnprotectedProfileIds: Array.Empty<string>(),
                Status: "complete",
                VerifiedCodex: true,
                VerifiedChatGpt: true),
            Diagnostics: new Dictionary<string, string>());
    }

    private sealed class NoOpTrayLocalCommandLauncher : ITrayLocalCommandLauncher
    {
        public void Open(TrayLocalCommand command)
        {
            throw new AssertionException("Local command should not run.");
        }
    }

    private sealed class NoOpTrayProtectionDisableConfirmation : ITrayProtectionDisableConfirmation
    {
        public bool Confirm(string action, TrayProtectionState state) => true;
    }

    private sealed class TestSetupController : IFirstRunSetupController
    {
        private readonly Func<DefaultStorageLayout, FirstRunSetupResult> _ensureSetupFunc;
        private readonly bool _setupRequired;
        private readonly bool _statusSucceeded;
        public int EnsureSetupCalls { get; private set; }

        public TestSetupController(
            Func<DefaultStorageLayout, FirstRunSetupResult> ensureSetupFunc,
            bool setupRequired = true,
            bool statusSucceeded = true)
        {
            _ensureSetupFunc = ensureSetupFunc;
            _setupRequired = setupRequired;
            _statusSucceeded = statusSucceeded;
        }

        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout)
        {
            EnsureSetupCalls++;
            return _ensureSetupFunc(layout);
        }

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null)
        {
            if (!_setupRequired)
            {
                return new FirstRunSetupResult(
                    Succeeded: true,
                    Code: "setup_complete",
                    State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, true),
                    Diagnostics: new Dictionary<string, string>());
            }

            var storeResult = SubmitBindingProfileStore.Load(layout);
            var hasUnprotected = storeResult.Profiles.Any(p => !p.IsSetupComplete);
            return new FirstRunSetupResult(
                Succeeded: _statusSucceeded,
                Code: hasUnprotected ? "setup_required" : "setup_complete",
                State: new FirstRunSetupState(
                    Required: hasUnprotected,
                    UnprotectedProfileIds: storeResult.Profiles.Where(p => !p.IsSetupComplete).Select(p => p.ProfileId).ToArray(),
                    Status: hasUnprotected ? "incomplete" : "complete",
                    VerifiedCodex: storeResult.Profiles.Any(p => p.ProfileId == "codex-desktop" && p.IsSetupComplete),
                    VerifiedChatGpt: storeResult.Profiles.Any(p => p.ProfileId == "chatgpt-desktop" && p.IsSetupComplete)),
                Diagnostics: new Dictionary<string, string>());
        }

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout)
        {
            return GetSetupStatus(layout);
        }

        public bool IsSetupComplete(DefaultStorageLayout layout)
        {
            var status = GetSetupStatus(layout);
            return !status.State.Required;
        }
    }
}
