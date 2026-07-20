using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

public partial class SanitizerTests
{
    [Test]
    public void PlainTextAttachmentIntake_ReadsTextFileIntoSanitizerContentPart()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(tempDirectory, "config.txt");
            File.WriteAllText(path, "password=P@ssw0rd!");
            var intake = PlainTextAttachmentIntake.ReadFile(path, "file-1");
            var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));

            var result = sanitizer.Sanitize(CreateRequestWithParts(new[] { intake.ContentPart }));

            Assert.That(intake.Succeeded, Is.True);
            Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
            Assert.That(result.SanitizedText, Does.Contain("PASSWORD_REDACTED"));
            Assert.That(result.Replacements.Single().ContentPartId, Is.EqualTo("file-1"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PlainTextAttachmentIntake_EnforcesSizeAndTypeLimitsWithoutRawContent()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var tooLargePath = Path.Combine(tempDirectory, "large.txt");
            var binaryPath = Path.Combine(tempDirectory, "archive.zip");
            File.WriteAllText(tooLargePath, "SECRET_CONTENT_SHOULD_NOT_LEAK");
            File.WriteAllText(binaryPath, "SECRET_CONTENT_SHOULD_NOT_LEAK");

            var tooLarge = PlainTextAttachmentIntake.ReadFile(tooLargePath, "large", new PlainTextAttachmentOptions(MaxBytes: 4));
            var unsupported = PlainTextAttachmentIntake.ReadFile(binaryPath, "archive");
            var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
            var result = sanitizer.Sanitize(CreateRequestWithParts(new[] { tooLarge.ContentPart, unsupported.ContentPart }));
            var serializedWarnings = System.Text.Json.JsonSerializer.Serialize(tooLarge.Warnings.Concat(unsupported.Warnings));

            Assert.That(tooLarge.Succeeded, Is.False);
            Assert.That(unsupported.Succeeded, Is.False);
            Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
            Assert.That(serializedWarnings, Does.Not.Contain("SECRET_CONTENT_SHOULD_NOT_LEAK"));
            Assert.That(AuditInspection.Contains(result.AuditEvent, "SECRET_CONTENT_SHOULD_NOT_LEAK"), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void LocalComposerShell_SafePromptSubmitsThroughOwnedPath()
    {
        var submitter = new RecordingPromptSubmitter();
        var composer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(null)));

        var outcome = composer.Submit("Normal prompt text");

        Assert.That(outcome.SubmitOutcome.Submitted, Is.True);
        Assert.That(submitter.SubmittedTexts.Single(), Is.EqualTo("Normal prompt text"));
    }

    [Test]
    public void LocalComposerShell_SensitivePromptSubmitsOnlyApprovedSanitizedText()
    {
        var submitter = new RecordingPromptSubmitter();
        var composer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(submitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm)));

        var outcome = composer.Submit("Connect to 192.168.10.25");

        Assert.That(outcome.SubmitOutcome.Submitted, Is.True);
        Assert.That(submitter.SubmittedTexts.Single(), Does.Contain("IP_"));
        Assert.That(submitter.SubmittedTexts.Single(), Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void LocalComposerShell_CancelAndBlockSubmitNothing()
    {
        var cancelSubmitter = new RecordingPromptSubmitter();
        var cancelComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(cancelSubmitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Cancel)));
        var blockSubmitter = new RecordingPromptSubmitter();
        var blockComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(blockSubmitter, new FixedConfirmationProvider(null)));

        var canceled = cancelComposer.Submit("Connect to 192.168.10.25");
        var blocked = blockComposer.Submit("Reject BLOCK_THIS");

        Assert.That(canceled.SubmitOutcome.Submitted, Is.False);
        Assert.That(cancelSubmitter.SubmittedTexts, Is.Empty);
        Assert.That(blocked.SubmitOutcome.Submitted, Is.False);
        Assert.That(blocked.SubmitOutcome.Status, Is.EqualTo("blocked"));
        Assert.That(blockSubmitter.SubmittedTexts, Is.Empty);
    }

    [Test]
    public void GatewayFailureRecovery_ConfirmationSubmitterAndAuditFailuresSendNothing()
    {
        var confirmationSubmitter = new RecordingPromptSubmitter();
        var confirmationComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(
                confirmationSubmitter,
                new FixedConfirmationProvider(_ => throw new InvalidOperationException("confirmation unavailable"))));
        var submitterComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            new SubmitOwningAdapter(
                new ThrowingPromptSubmitter(),
                new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm)));
        var auditSubmitter = new RecordingPromptSubmitter();
        var auditComposer = new LocalComposerShell(
            new Sanitizer(
                new InMemoryHmacMappingVault(TestSecret()),
                Array.Empty<DictionaryTerm>(),
                RedactionPolicy.BuiltInDefaults,
                secretScanner: null,
                auditSink: new FailingAuditSink()),
            new SubmitOwningAdapter(auditSubmitter, new FixedConfirmationProvider(ConfirmationDecisionContract.Confirm)));

        var confirmationFailed = confirmationComposer.Submit("Connect to 192.168.10.25");
        var submitFailed = submitterComposer.Submit("Connect to 192.168.10.25");
        var auditFailed = auditComposer.Submit("Connect to 192.168.10.25");

        Assert.That(confirmationFailed.SubmitOutcome.Status, Is.EqualTo("confirmation_failed"));
        Assert.That(confirmationSubmitter.SubmittedTexts, Is.Empty);
        Assert.That(submitFailed.SubmitOutcome.Status, Is.EqualTo("submit_failed"));
        Assert.That(auditFailed.SubmitOutcome.Status, Is.EqualTo("blocked"));
        Assert.That(auditSubmitter.SubmittedTexts, Is.Empty);
    }

    [Test]
    public void RestorationHandoff_RestoresLocalSensitiveOutputAndBlocksResubmission()
    {
        var vault = new InMemoryHmacMappingVault(TestSecret());
        var pseudonym = vault.GetOrCreatePseudonym("ip_address", "192.168.10.25");
        var handoff = RestorationHandoff.RestoreAndEvaluate(
            new LocalRestorer(vault),
            new RestoreRequest(
                SanitizedText: $"Connect to {pseudonym} with TOKEN_REDACTED",
                Replacements: new[]
                {
                    new Replacement("prompt", 11, pseudonym.Length, "ip_address", pseudonym, PolicyActions.PseudonymizeRestorable, Restorable: true),
                    new Replacement("prompt", 30, "TOKEN_REDACTED".Length, "token", "TOKEN_REDACTED", PolicyActions.RedactNonRestorable, Restorable: false)
                }));

        Assert.That(handoff.Restoration.Metadata.LocalSensitive, Is.True);
        Assert.That(handoff.Restoration.Text, Does.Contain("192.168.10.25"));
        Assert.That(handoff.Restoration.Text, Does.Contain("TOKEN_REDACTED"));
        Assert.That(handoff.SubmitDecision.CanSubmit, Is.False);
        Assert.That(handoff.SubmitDecision.Warnings.Single().Code, Is.EqualTo("local_sensitive_resubmission_blocked"));
    }

    [Test]
    public void ReadinessDoctor_ReportsPolicyVaultAuditAndScannerStatusWithoutRawValues()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var manifest = CreatePackageSmokeManifest();

            var report = ReadinessDoctor.Check(layout, manifest, TestSecret);
            var serialized = System.Text.Json.JsonSerializer.Serialize(report);

            Assert.That(report.Items.Select(item => item.Component), Is.SupersetOf(new[] { "policy", "vault", "audit", "scanner" }));
            Assert.That(report.Items.Single(item => item.Component == "vault_secret").Code, Is.EqualTo("vault_secret_ready"));
            Assert.That(report.Items.Single(item => item.Component == "scanner").Code, Is.EqualTo("scanner_ready"));
            Assert.That(serialized, Does.Not.Contain("SENSITIVE_MARKER"));
            Assert.That(serialized, Does.Not.Contain("ACME Banking"));
            Assert.That(serialized, Does.Not.Contain("unit-test-secret"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ReadinessDoctor_MissingScannerBinaryIsRawFreeFailure()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var manifest = CreatePackageSmokeManifest(includeGitleaksBinary: false);

            var report = ReadinessDoctor.Check(layout, manifest, TestSecret);

            Assert.That(report.Ready, Is.False);
            Assert.That(report.Items.Single(item => item.Component == "scanner").Code, Is.EqualTo("scanner_binary_missing"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ReadinessDoctor_VaultSecretFailureIsRawFreeFailure()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var manifest = CreatePackageSmokeManifest();

            var report = ReadinessDoctor.Check(
                layout,
                manifest,
                () => throw new InvalidOperationException("secret unavailable"));

            Assert.That(report.Ready, Is.False);
            Assert.That(report.Items.Single(item => item.Component == "vault_secret").Code, Is.EqualTo("vault_secret_unavailable"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_AddListRemoveUsesSafeSummaries()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new ManagedSensitiveDictionary(Path.Combine(tempDirectory, "managed-dictionary.csv"));

            var add = store.Add("customer", "ACME Banking", "known customer");
            var addUrl = store.Add("url", "https://internal.example.local", null);
            var addUsername = store.Add("username", "user1", null);
            var summaries = store.ListSummaries();
            var remove = store.Remove(add.EntryId!);

            Assert.That(add.Succeeded, Is.True);
            Assert.That(addUrl.Succeeded, Is.True);
            Assert.That(addUsername.Succeeded, Is.True);
            Assert.That(summaries.Single(entry => entry.Id == add.EntryId).Type, Is.EqualTo("customer"));
            Assert.That(summaries.Single(entry => entry.Id == add.EntryId).ValueLength, Is.EqualTo("ACME Banking".Length));
            Assert.That(summaries.Single(entry => entry.Id == addUrl.EntryId).Type, Is.EqualTo("url"));
            Assert.That(summaries.Single(entry => entry.Id == addUsername.EntryId).Type, Is.EqualTo("username"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(summaries), Does.Not.Contain("ACME Banking"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(summaries), Does.Not.Contain("internal.example.local"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(summaries), Does.Not.Contain("user1"));
            Assert.That(remove.Succeeded, Is.True);
            Assert.That(store.ListSummaries().Select(entry => entry.Id), Does.Not.Contain(add.EntryId));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_LoadTermsFeedsSanitizer()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new ManagedSensitiveDictionary(Path.Combine(tempDirectory, "managed-dictionary.csv"));
            store.Add("customer", "ACME Banking", null);
            store.Add("username", "user1", null);
            var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), store.LoadTerms());

            var result = sanitizer.Sanitize(CreatePromptRequest("Talk to ACME Banking from C:\\Users\\user1>"));

            Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Confirm));
            Assert.That(result.SanitizedText, Does.Not.Contain("ACME Banking"));
            Assert.That(result.SanitizedText, Does.Not.Contain("user1"));
            Assert.That(result.Replacements.Select(replacement => replacement.Type), Does.Contain("username"));
            Assert.That(AuditInspection.Contains(result.AuditEvent, "ACME Banking"), Is.False);
            Assert.That(AuditInspection.Contains(result.AuditEvent, "user1"), Is.False);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_AddBatchRejectsDuplicatesWithoutPartialWrite()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new ManagedSensitiveDictionary(Path.Combine(tempDirectory, "managed-dictionary.csv"));
            store.Add("customer", "ACME Banking", null);

            var result = store.AddBatch(new[]
            {
                new DictionaryTerm("domain", "corp.example.local", PolicyActions.PseudonymizeRestorable, null),
                new DictionaryTerm("username", "user1", PolicyActions.PseudonymizeRestorable, null),
                new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null)
            });
            var terms = store.LoadTerms();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("dictionary_batch_rejected"));
            Assert.That(result.Items.Single(item => item.Type == "customer").Code, Is.EqualTo("dictionary_term_exists"));
            Assert.That(terms, Has.Count.EqualTo(1));
            Assert.That(terms.Single().Value, Is.EqualTo("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedSensitiveDictionary_ImportCsvReplacesOnlyAfterValidation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new ManagedSensitiveDictionary(Path.Combine(tempDirectory, "managed-dictionary.csv"));
            store.Add("customer", "SAFE_CUSTOMER", null);
            var validPath = Path.Combine(tempDirectory, "valid.csv");
            var invalidPath = Path.Combine(tempDirectory, "invalid.csv");
            File.WriteAllText(validPath, """
                type,value,action,notes
                username,user1,pseudonymize_restorable,Windows account
                """);
            File.WriteAllText(invalidPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,Known customer
                """);

            var valid = store.ImportCsv(validPath);
            var invalid = store.ImportCsv(invalidPath);
            var terms = store.LoadTerms();

            Assert.That(valid.Activated, Is.True);
            Assert.That(invalid.Activated, Is.False);
            Assert.That(terms.Single().Type, Is.EqualTo("username"));
            Assert.That(terms.Single().Value, Is.EqualTo("user1"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PolicyActivationStore_PromotesValidPolicyAndRejectsInvalidCandidate()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new PolicyActivationStore(tempDirectory);
            var valid = store.PromoteCandidate(ValidPolicyText("active-policy"));
            var invalid = store.PromoteCandidate("""
                version = 1
                profile = "invalid-policy"

                [defaults]
                secret = "send_raw_prompt"
                """);

            Assert.That(valid.Activated, Is.True);
            Assert.That(valid.ActivePolicy.Profile, Is.EqualTo("active-policy"));
            Assert.That(invalid.Activated, Is.False);
            Assert.That(invalid.ActivePolicy.Profile, Is.EqualTo("active-policy"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PolicyActivationStore_ValidatesDictionaryCandidateBeforeActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new PolicyActivationStore(tempDirectory);
            var valid = store.PromoteDictionaryCandidate("""
                type,value,action,notes
                customer,ACME Banking,pseudonymize_restorable,known customer
                """);
            var invalid = store.PromoteDictionaryCandidate("""
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,known customer
                """);

            Assert.That(valid.Activated, Is.True);
            Assert.That(valid.ActiveTerms.Single().Type, Is.EqualTo("customer"));
            Assert.That(invalid.Activated, Is.False);
            Assert.That(invalid.ActiveTerms.Single().Type, Is.EqualTo("customer"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ManagedPolicyRules_AddsUrlPrefixAndRegexThroughStagedActivation()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var rules = new ManagedPolicyRules(tempDirectory);

            var url = rules.AddUrlPrefix("https://internal.example.local/");
            var regex = rules.AddRegexRule("project", "\\bPRJ-[0-9]{4}\\b");
            var active = new TomlPolicyLoader().LoadOrDefault(PolicyActivationStore.ActivePolicyPath(tempDirectory));

            Assert.That(url.Succeeded, Is.True);
            Assert.That(regex.Succeeded, Is.True);
            Assert.That(active.ActivePolicy.SensitiveRules.Single().Type, Is.EqualTo("url"));
            Assert.That(active.ActivePolicy.RegexRules.Single().Type, Is.EqualTo("project"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProductionSanitizer_LoadsActiveManagedPolicyAndManagedDictionary()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var rules = new ManagedPolicyRules(layout.PolicyDirectory);
            var dictionary = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));

            var url = rules.AddUrlPrefix("https://sensitive.example.com/internal/");
            var customer = dictionary.Add("customer", "ACME Banking", null);
            var sanitizer = Sanitizer.CreateProduction(layout);

            var urlResult = sanitizer.Sanitize(CreatePromptRequest("Open https://sensitive.example.com/internal/build"));
            var dictionaryResult = sanitizer.Sanitize(CreatePromptRequest("Talk to ACME Banking"));

            Assert.That(url.Succeeded, Is.True);
            Assert.That(customer.Succeeded, Is.True);
            Assert.That(urlResult.Decision, Is.EqualTo(SanitizeDecision.Confirm));
            Assert.That(urlResult.Replacements.Single().Type, Is.EqualTo("url"));
            Assert.That(urlResult.SanitizedText, Does.Not.Contain("sensitive.example.com"));
            Assert.That(dictionaryResult.Decision, Is.EqualTo(SanitizeDecision.Confirm));
            Assert.That(dictionaryResult.SanitizedText, Does.Not.Contain("ACME Banking"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProductionPolicyLoad_FallsBackToLastKnownGoodWithRawFreeDiagnostics()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var store = new PolicyActivationStore(layout.PolicyDirectory);
            store.PromoteCandidate(ValidPolicyText("last-good"));
            store.PromoteCandidate(ValidPolicyText("current-good"));
            File.WriteAllText(PolicyActivationStore.ActivePolicyPath(layout.PolicyDirectory), """
                version = 1
                profile = "invalid-policy"

                [defaults]
                secret = "send_raw_prompt"
                """);

            var result = Sanitizer.LoadProductionPolicy(layout);
            var serialized = System.Text.Json.JsonSerializer.Serialize(result.Diagnostics);

            Assert.That(result.Activated, Is.True);
            Assert.That(result.Source, Is.EqualTo("managed-last-known-good"));
            Assert.That(result.ActivePolicy.Profile, Is.EqualTo("last-good"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo("invalid_policy_rejected"));
            Assert.That(result.Diagnostics.RuleCounts.Values.Sum(), Is.EqualTo(0));
            Assert.That(serialized, Does.Not.Contain("send_raw_prompt"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProductionPolicyLoad_ReportsActiveSourceProfileAndRuleCountsWithoutRawRules()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var rules = new ManagedPolicyRules(layout.PolicyDirectory);

            rules.AddUrlPrefix("https://sensitive.example.com/internal/");
            var result = Sanitizer.LoadProductionPolicy(layout);
            var serialized = System.Text.Json.JsonSerializer.Serialize(result.Diagnostics);

            Assert.That(result.Source, Is.EqualTo("managed-active"));
            Assert.That(result.Diagnostics.SourcePrecedence, Is.EqualTo(new[] { "managed-active" }));
            Assert.That(result.Diagnostics.ActiveProfileIds, Is.EqualTo(new[] { "managed-policy" }));
            Assert.That(result.Diagnostics.RuleCounts["sensitive"], Is.EqualTo(1));
            Assert.That(serialized, Does.Not.Contain("sensitive.example.com"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PolicyActivationStore_RollbackRestoresPreviousPolicy()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var store = new PolicyActivationStore(tempDirectory);
            store.PromoteCandidate(ValidPolicyText("previous-policy"));
            store.PromoteCandidate(ValidPolicyText("current-policy"));

            var rollback = store.Rollback();

            Assert.That(rollback.Activated, Is.True);
            Assert.That(rollback.ActivePolicy.Profile, Is.EqualTo("previous-policy"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PolicyPrecedenceReporter_ReportsRawFreeDeterministicPrecedence()
    {
        var global = RedactionPolicy.BuiltInDefaults with
        {
            Profile = "global",
            BlockRules = new[] { new PolicyRule("url", null, "SECRET_INTERNAL_HOST", "contains", PolicyActions.Block, null, null) }
        };
        var project = RedactionPolicy.BuiltInDefaults with
        {
            Profile = "project",
            AllowRules = new[] { new PolicyRule("url", "https://SECRET_INTERNAL_HOST", null, "prefix", PolicyActions.Allow, null, null) }
        };

        var report = PolicyPrecedenceReporter.Build(new[]
        {
            new PolicySource("global", global),
            new PolicySource("project", project)
        });
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.That(report.SourcePrecedence, Is.EqualTo(new[] { "global", "project" }));
        Assert.That(report.ActiveProfileIds, Is.EqualTo(new[] { "global", "project" }));
        Assert.That(report.WinningSourceByArea["allow"], Is.EqualTo("project"));
        Assert.That(report.WinningSourceByArea["block"], Is.EqualTo("global"));
        Assert.That(report.WinningSourceByArea["conflicts"], Is.EqualTo("last_source_wins"));
        Assert.That(serialized, Does.Not.Contain("SECRET_INTERNAL_HOST"));
    }

    [Test]
    public void FileAuditSink_WritesTamperEvidentHashChain()
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

            var verification = AuditChainVerifier.Verify(layout.AuditDirectory);
            var payload = string.Join("\n", Directory.GetFiles(layout.AuditDirectory).Select(File.ReadAllText));

            Assert.That(verification.Valid, Is.True);
            Assert.That(verification.EventCount, Is.EqualTo(2));
            Assert.That(payload, Does.Contain("previous_hash"));
            Assert.That(payload, Does.Not.Contain("192.168.10.25"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void AuditChainVerifier_DetectsModifiedRemovedAndReorderedEvents()
    {
        AssertBrokenAuditChain(files =>
        {
            var file = files[1];
            File.WriteAllText(file, File.ReadAllText(file).Replace("Confirm", "Allow", StringComparison.Ordinal));
        });
        AssertBrokenAuditChain(files => File.Delete(files[1]));
        AssertBrokenAuditChain(files => File.Delete(files[^1]));
        AssertBrokenAuditChain(files =>
        {
            var first = files[0];
            var second = files[1];
            var temp = Path.Combine(Path.GetDirectoryName(first)!, "audit-temp-swap.json");
            File.Move(first, temp);
            File.Move(second, first);
            File.Move(temp, second);
        });
    }

    [Test]
    public void AuditSummaryReporter_ReportsCountsAndBrokenChainWithoutRawValues()
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
            sanitizer.Sanitize(CreatePromptRequest("Reject BLOCK_THIS"));
            var files = Directory.GetFiles(layout.AuditDirectory, "audit-*.json").OrderBy(path => path).ToArray();
            File.WriteAllText(files[1], File.ReadAllText(files[1]).Replace("Block", "Allow", StringComparison.Ordinal));

            var summary = AuditSummaryReporter.Summarize(layout.AuditDirectory);
            var serialized = System.Text.Json.JsonSerializer.Serialize(summary);

            Assert.That(summary.Chain.Valid, Is.False);
            Assert.That(summary.Chain.Code, Is.EqualTo("audit_chain_hash_mismatch"));
            Assert.That(summary.DecisionCounts.Keys, Does.Contain("Allow"));
            Assert.That(summary.WarningCodeCounts.Keys, Does.Contain("synthetic_block_marker"));
            Assert.That(summary.FirstEvent, Is.Not.Null);
            Assert.That(summary.LastEvent, Is.Not.Null);
            Assert.That(serialized, Does.Not.Contain("BLOCK_THIS"));
            Assert.That(serialized, Does.Not.Contain("Normal prompt text"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void FileAuditSink_RetentionKeepsRemainingAuditChainValid()
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

            var verification = AuditChainVerifier.Verify(layout.AuditDirectory);

            Assert.That(verification.Valid, Is.True);
            Assert.That(verification.EventCount, Is.EqualTo(2));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ScannerRuntimeConfigurationValidator_RequiresMatchingBinaryChecksum()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = ScannerRuntimeConfigurationValidator.Validate(manifest);

        Assert.That(report.Valid, Is.True);
        Assert.That(report.BinaryChecksumMatches, Is.True);
    }

    [Test]
    public void ScannerRuntimeConfigurationValidator_MismatchedChecksumIsFatalRawFreeConfigurationProblem()
    {
        var manifest = CreatePackageSmokeManifest();
        File.WriteAllText(manifest.GitleaksBinaryPath, "tampered scanner artifact");

        var report = ScannerRuntimeConfigurationValidator.Validate(manifest);
        var scanner = new ScannerConfigurationGuardedSecretScanner(
            new RecordingSecretScanner(new SecretScanResult(false, ScannerStatusIds.NoFindings.Value, Array.Empty<GitleaksFindingSpan>())),
            () => report);
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()), Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults, scanner);
        var result = sanitizer.Sanitize(CreatePromptRequest("Normal prompt text"));

        Assert.That(report.Valid, Is.False);
        Assert.That(report.WarningCode, Is.EqualTo("scanner_checksum_mismatch"));
        Assert.That(result.Decision, Is.EqualTo(SanitizeDecision.Block));
        Assert.That(result.AuditEvent.ScannerStatuses["gitleaks"], Is.EqualTo("configuration_error"));
    }

    [Test]
    public void MvpPackageSmoke_ValidatesReleasePackageManifestShape()
    {
        var manifest = CreatePackageSmokeManifest();

        var report = MvpPackageSmokeRunner.Run(manifest, TestSecret());

        Assert.That(report.ReleasePackageManifestSmokePassed, Is.True);
        Assert.That(report.ScannerChecksumMatched, Is.True);
        Assert.That(report.RequiresGit, Is.False);
        Assert.That(report.RequiresGo, Is.False);
        Assert.That(report.RequiresNetwork, Is.False);
    }

    [Test]
    public void ReleaseReadinessSmoke_CoversOperationalReadinessMatrix()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var manifest = CreatePackageSmokeManifest();

            var report = ReleaseReadinessSmokeRunner.Run(tempDirectory, manifest, TestSecret());

            Assert.That(report.Passed, Is.True);
            Assert.That(report.PolicyActivationAndPrecedencePassed, Is.True);
            Assert.That(report.AuditChainVerificationPassed, Is.True);
            Assert.That(report.ScannerPackageValidationPassed, Is.True);
            Assert.That(report.AttachmentIntakePassed, Is.True);
            Assert.That(report.GatewayHandoffPassed, Is.True);
            Assert.That(report.RestorationHandoffPassed, Is.True);
            Assert.That(report.OsAdapterDemoPassed, Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
