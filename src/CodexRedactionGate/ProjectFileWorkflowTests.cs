using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using CodexRedactionGate;

public sealed class ProjectFileWorkflowTests
{
    [Test]
    public void TrayStatusFormatter_ReportsComposerAndProjectFileProtectionSeparately()
    {
        var status = TrayStatusFormatter.FormatMenuStatus(new TrayProtectionState(
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
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: true));

        Assert.That(status, Does.Contain("composer_protected=true"));
        Assert.That(status, Does.Contain("project_files_protected=false"));
        Assert.That(status, Does.Contain("project_file_status=not_configured"));
        Assert.That(status, Does.Contain("protected_send_binding=Enter"));
    }

    [Test]
    public void ProjectFileContextBroker_CreatesSanitizedVirtualFileWithRawFreeDiagnostics()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var filePath = Path.Combine(tempDirectory, "config.txt");
            File.WriteAllText(filePath, "Connect to 192.168.10.25\r\nRead C:\\Users\\user1\\secret.txt\r\npassword=P@ssw0rd!");
            var broker = new ProjectFileContextBroker(
                TestSanitizers.Create(),
                DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data")));

            var result = broker.CreateSanitizedVirtualFile(filePath);
            var diagnostics = JsonSerializer.Serialize(result.Diagnostics);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Code, Is.EqualTo("sanitized_virtual_file_ready"));
            Assert.That(result.VirtualFile, Is.Not.Null);
            Assert.That(
                result.VirtualFile!.VirtualPath.StartsWith("file_", StringComparison.Ordinal)
                || result.VirtualFile.VirtualPath.StartsWith("PATH_", StringComparison.Ordinal),
                Is.True);
            Assert.That(result.VirtualFile.VirtualPath, Does.Not.Contain(filePath));
            Assert.That(result.VirtualFile.SanitizedText, Does.Not.Contain("192.168.10.25"));
            Assert.That(result.VirtualFile.SanitizedText, Does.Not.Contain("user1"));
            Assert.That(result.VirtualFile.SanitizedText, Does.Not.Contain("P@ssw0rd!"));
            Assert.That(result.VirtualFile.ReplacementCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(result.Diagnostics["content_hash"], Is.EqualTo(result.VirtualFile.ContentHash));
            Assert.That(result.Diagnostics.Keys.Any(key => key.StartsWith("entity_count.", StringComparison.Ordinal)), Is.True);
            Assert.That(diagnostics, Does.Not.Contain(filePath));
            Assert.That(diagnostics, Does.Not.Contain("192.168.10.25"));
            Assert.That(diagnostics, Does.Not.Contain("P@ssw0rd!"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProtectedWorkspaceStore_RegistersWorkspaceByRawFreeId()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);

            var register = ProtectedWorkspaceStore.Protect(layout, workspace);
            var status = ProtectedWorkspaceStore.GetStatus(layout, workspace);
            var stored = File.ReadAllText(register.StorePath);

            Assert.That(register.Succeeded, Is.True);
            Assert.That(status.Protected, Is.True);
            Assert.That(register.WorkspaceId, Does.StartWith("file_"));
            Assert.That(stored, Does.Not.Contain(workspace));
            Assert.That(stored, Does.Contain(register.WorkspaceId));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileContextBroker_ProtectedWorkspaceGuardFailsClosedForUnregisteredOutsideAndUnsupportedFiles()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            var outside = Path.Combine(tempDirectory, "outside");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            var protectedFile = Path.Combine(workspace, "config.txt");
            var outsideFile = Path.Combine(outside, "config.txt");
            var unsupportedFile = Path.Combine(workspace, "archive.zip");
            var tooLargeFile = Path.Combine(workspace, "large.txt");
            File.WriteAllText(protectedFile, "Connect to 192.168.10.25");
            File.WriteAllText(outsideFile, "Connect to 192.168.10.25");
            File.WriteAllText(unsupportedFile, "Connect to 192.168.10.25");
            File.WriteAllText(tooLargeFile, "Connect to 192.168.10.25");
            var broker = new ProjectFileContextBroker(
                TestSanitizers.Create(),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var tinyBroker = new ProjectFileContextBroker(
                TestSanitizers.Create(),
                layout,
                new ProjectFileBrokerOptions(MaxBytes: 4, RequireProtectedWorkspace: true));

            var unregistered = broker.CreateSanitizedVirtualFile(protectedFile, workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var outsideResult = broker.CreateSanitizedVirtualFile(outsideFile, workspace);
            var unsupported = broker.CreateSanitizedVirtualFile(unsupportedFile, workspace);
            var tooLarge = tinyBroker.CreateSanitizedVirtualFile(tooLargeFile, workspace);

            Assert.That(unregistered.Succeeded, Is.False);
            Assert.That(unregistered.Code, Is.EqualTo("protected_workspace_not_registered"));
            Assert.That(outsideResult.Succeeded, Is.False);
            Assert.That(outsideResult.Code, Is.EqualTo("outside_workspace"));
            Assert.That(unsupported.Succeeded, Is.False);
            Assert.That(unsupported.Code, Is.EqualTo("unsupported_attachment_type"));
            Assert.That(tooLarge.Succeeded, Is.False);
            Assert.That(tooLarge.Code, Is.EqualTo("attachment_too_large"));
            Assert.That(JsonSerializer.Serialize(unsupported.Diagnostics), Does.Not.Contain("192.168.10.25"));
            Assert.That(JsonSerializer.Serialize(tooLarge.Diagnostics), Does.Not.Contain("192.168.10.25"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Program_ProjectFileCommands_PrintRawFreeStatusAndSanitizedVirtualFile()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var filePath = Path.Combine(workspace, "config.txt");
            var originalText = "Connect to 192.168.10.25\r\npassword=P@ssw0rd!";
            File.WriteAllText(filePath, originalText);
            var runtime = CreateRuntime(layout);

            var protect = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-workspace-protect", workspace }, runtime));
            var sanitize = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-sanitize", filePath, "--protected-workspace", workspace }, runtime));

            Assert.That(protect.ExitCode, Is.EqualTo(0));
            Assert.That(protect.Stdout, Does.Contain("status: protected_workspace_registered"));
            Assert.That(protect.Stdout, Does.Not.Contain(workspace));
            Assert.That(sanitize.ExitCode, Is.EqualTo(0));
            Assert.That(sanitize.Stdout, Does.Contain("status: sanitized_virtual_file_ready"));
            Assert.That(sanitize.Stdout, Does.Contain("project_files_protected: false"));
            Assert.That(sanitize.Stdout, Does.Contain("sanitized_virtual_file:"));
            Assert.That(sanitize.Stdout, Does.Not.Contain(filePath));
            Assert.That(sanitize.Stdout, Does.Not.Contain("192.168.10.25"));
            Assert.That(sanitize.Stdout, Does.Not.Contain("P@ssw0rd!"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileReadOnlySmokeRunner_ProvesSanitizedReadOnlyPayload()
    {
        var report = ProjectFileReadOnlySmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("project-file-smoke-test-secret"));
        var rendered = string.Join(Environment.NewLine, ProjectFileReadOnlySmokeRunner.RenderRawFree(report));

        Assert.That(report.Passed, Is.True);
        Assert.That(report.WorkspaceRegistered, Is.True);
        Assert.That(report.ReadSucceeded, Is.True);
        Assert.That(report.PayloadSanitized, Is.True);
        Assert.That(report.RawFreeEvidence, Is.True);
        Assert.That(report.LiveProjectFilesProtected, Is.False);
        Assert.That(rendered, Does.Contain("project_file_smoke: passed"));
        Assert.That(rendered, Does.Contain("live_project_files_protected: false"));
        Assert.That(rendered, Does.Not.Contain("deploy.corp.example.local"));
        Assert.That(rendered, Does.Not.Contain("user1"));
        Assert.That(rendered, Does.Not.Contain("P@ssw0rd!"));
    }

    [Test]
    public void ProjectFileContextBroker_SanitizesManagedToolOutputAndReportsUnmanagedAsUnprotected()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var broker = new ProjectFileContextBroker(
                TestSanitizers.Create(),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);

            var result = broker.SanitizeManagedToolOutput(
                "Read C:\\Users\\user1\\secret.txt from 192.168.10.25 with password=P@ssw0rd!",
                workspace,
                "read-file");
            var unmanaged = ProjectFileContextBroker.ReportUnmanagedToolOutput(workspace);
            var diagnostics = JsonSerializer.Serialize(result.Diagnostics);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Code, Is.EqualTo("managed_tool_output_sanitized"));
            Assert.That(result.ToolOutput, Is.Not.Null);
            Assert.That(result.ToolOutput!.SanitizedText, Does.Not.Contain("user1"));
            Assert.That(result.ToolOutput.SanitizedText, Does.Not.Contain("192.168.10.25"));
            Assert.That(result.ToolOutput.SanitizedText, Does.Not.Contain("P@ssw0rd!"));
            Assert.That(result.ToolOutput.SanitizedText, Does.Contain("PASSWORD_REDACTED"));
            Assert.That(result.Diagnostics["tool_output_managed"], Is.EqualTo("true"));
            Assert.That(result.Diagnostics["raw_tool_output_recorded"], Is.EqualTo("false"));
            Assert.That(result.Diagnostics.Keys.Any(key => key.StartsWith("entity_count.", StringComparison.Ordinal)), Is.True);
            Assert.That(diagnostics, Does.Not.Contain("user1"));
            Assert.That(diagnostics, Does.Not.Contain("192.168.10.25"));
            Assert.That(diagnostics, Does.Not.Contain("P@ssw0rd!"));

            Assert.That(unmanaged.Succeeded, Is.False);
            Assert.That(unmanaged.Code, Is.EqualTo("unmanaged_tool_output_unprotected"));
            Assert.That(unmanaged.Diagnostics["tool_output_managed"], Is.EqualTo("false"));
            Assert.That(JsonSerializer.Serialize(unmanaged.Diagnostics), Does.Not.Contain(workspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFilePatchDryRun_RestoresPreviewWithoutWritingAndBlocksStaleSource()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var filePath = Path.Combine(workspace, "config.txt");
            var originalText = "Connect to 192.168.10.25\r\npassword=P@ssw0rd!";
            File.WriteAllText(filePath, originalText);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var vault = new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("project-patch-dry-run-test-secret"));
            var broker = new ProjectFileContextBroker(
                new Sanitizer(vault),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var source = broker.CreateSanitizedVirtualFile(filePath, workspace);
            var restoreWorkflow = new LocalRestoreWorkflow(
                new LocalRestorer(vault),
                new FileAuditSink(Path.Combine(tempDirectory, "audit")));
            var dryRun = new ProjectFilePatchDryRun(request => restoreWorkflow.Restore(request).Restoration, layout);
            var sanitizedEdit = source.VirtualFile!.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal);
            var unrelatedPseudonym = vault.GetOrCreatePseudonym("domain", "other.workspace.example.local");

            var preview = dryRun.Preview(new ProjectFilePatchDryRunRequest(
                source.VirtualFile,
                workspace,
                filePath,
                sanitizedEdit));
            var unrelated = dryRun.Preview(new ProjectFilePatchDryRunRequest(
                source.VirtualFile,
                workspace,
                filePath,
                sanitizedEdit + Environment.NewLine + unrelatedPseudonym));
            File.WriteAllText(filePath, "changed outside broker");
            var stale = dryRun.Preview(new ProjectFilePatchDryRunRequest(
                source.VirtualFile,
                workspace,
                filePath,
                sanitizedEdit));

            Assert.That(preview.Succeeded, Is.True);
            Assert.That(preview.Code, Is.EqualTo("restore_aware_patch_preview_ready"));
            Assert.That(preview.PreviewText, Does.Contain("Route to 192.168.10.25"));
            Assert.That(preview.PreviewText, Does.Contain("PASSWORD_REDACTED"));
            Assert.That(preview.LocalSensitive, Is.True);
            Assert.That(preview.Diagnostics["write_performed"], Is.EqualTo("false"));
            Assert.That(preview.Diagnostics["raw_patch_recorded"], Is.EqualTo("false"));
            Assert.That(File.ReadAllText(filePath), Is.EqualTo("changed outside broker"));
            Assert.That(unrelated.Succeeded, Is.False);
            Assert.That(unrelated.Code, Is.EqualTo("unrelated_pseudonym_in_patch"));
            Assert.That(stale.Succeeded, Is.False);
            Assert.That(stale.Code, Is.EqualTo("source_version_mismatch"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFilePatchApplier_WritesOnlyApprovedRestoredPatchAndAuditsRawFreeStatus()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var filePath = Path.Combine(workspace, "config.txt");
            var originalText = "Connect to 192.168.10.25\r\npassword=P@ssw0rd!";
            File.WriteAllText(filePath, originalText);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var vault = new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("project-patch-apply-test-secret"));
            var broker = new ProjectFileContextBroker(
                new Sanitizer(vault),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var source = broker.CreateSanitizedVirtualFile(filePath, workspace).VirtualFile!;
            var restoreWorkflow = new LocalRestoreWorkflow(
                new LocalRestorer(vault),
                new FileAuditSink(Path.Combine(tempDirectory, "restore-audit")));
            var applier = new ProjectFilePatchApplier(
                new ProjectFilePatchDryRun(request => restoreWorkflow.Restore(request).Restoration, layout),
                new FileAuditSink(layout.AuditDirectory));
            var request = new ProjectFilePatchDryRunRequest(
                source,
                workspace,
                filePath,
                source.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal));

            var canceled = applier.Apply(new ProjectFilePatchApplyRequest(request, Approved: false));
            var approved = applier.Apply(new ProjectFilePatchApplyRequest(request, Approved: true));
            var persistedAudit = string.Join(Environment.NewLine, Directory.GetFiles(layout.AuditDirectory, "audit-*.json").Select(File.ReadAllText));

            Assert.That(canceled.Succeeded, Is.False);
            Assert.That(canceled.Code, Is.EqualTo("project_patch_apply_cancelled"));
            Assert.That(canceled.Written, Is.False);
            Assert.That(approved.Succeeded, Is.True);
            Assert.That(approved.Code, Is.EqualTo("project_patch_applied"));
            Assert.That(approved.Written, Is.True);
            Assert.That(approved.LocalSensitive, Is.True);
            Assert.That(approved.Diagnostics["raw_restored_patch_recorded"], Is.EqualTo("false"));
            Assert.That(persistedAudit, Does.Contain(approved.Diagnostics["source_id"]));
            Assert.That(persistedAudit, Does.Contain(approved.Diagnostics["expected_content_hash"]));
            Assert.That(persistedAudit, Does.Contain(approved.Diagnostics["restored_patch_hash"]));
            Assert.That(File.ReadAllText(filePath), Does.Contain("Route to 192.168.10.25"));
            Assert.That(File.ReadAllText(filePath), Does.Contain("PASSWORD_REDACTED"));
            Assert.That(persistedAudit, Does.Contain("project_patch_applied"));
            Assert.That(persistedAudit, Does.Not.Contain("192.168.10.25"));
            Assert.That(persistedAudit, Does.Not.Contain("P@ssw0rd!"));
            Assert.That(persistedAudit, Does.Not.Contain(filePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFilePatchApplier_BlocksWriteWhenTargetEscapesAfterPreview()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Pass("Windows link escape regression is Windows-specific.");
        }

        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            var outside = Path.Combine(tempDirectory, "outside");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            var filePath = Path.Combine(workspace, "config.txt");
            var outsideFile = Path.Combine(outside, "config.txt");
            File.WriteAllText(filePath, "Connect to 192.168.10.25");
            File.WriteAllText(outsideFile, "outside");
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var vault = new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("project-patch-escape-test-secret"));
            var broker = new ProjectFileContextBroker(new Sanitizer(vault), layout, ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var source = broker.CreateSanitizedVirtualFile(filePath, workspace).VirtualFile!;
            var restoreWorkflow = new LocalRestoreWorkflow(new LocalRestorer(vault), new FileAuditSink(Path.Combine(tempDirectory, "restore-audit")));
            var applier = new ProjectFilePatchApplier(
                new ProjectFilePatchDryRun(request => restoreWorkflow.Restore(request).Restoration, layout),
                new FileAuditSink(layout.AuditDirectory));
            var request = new ProjectFilePatchDryRunRequest(
                source,
                workspace,
                filePath,
                source.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal));
            File.Delete(filePath);
            File.CreateSymbolicLink(filePath, outsideFile);

            var result = applier.Apply(new ProjectFilePatchApplyRequest(request, Approved: true));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("outside_workspace"));
            Assert.That(result.Written, Is.False);
            Assert.That(File.ReadAllText(outsideFile), Is.EqualTo("outside"));
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Pass("Creating symbolic links requires privileges on this Windows host.");
        }
        catch (IOException) when (!File.Exists(Path.Combine(tempDirectory, "workspace", "config.txt")))
        {
            Assert.Pass("Creating symbolic links requires privileges on this Windows host.");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileBypassGuard_FailsClosedForProtectedWorkspaceWithoutRawValues()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);

            var directAttachment = ProjectFileBypassGuard.ReportDirectAttachment(layout, workspace);
            var connector = ProjectFileBypassGuard.ReportUnmanagedConnector(layout, workspace);
            var broker = new ProjectFileContextBroker(TestSanitizers.Create(), layout, ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var managed = broker.SanitizeManagedToolOutput("safe output", workspace);
            var rendered = JsonSerializer.Serialize(new
            {
                DirectDiagnostics = directAttachment.Diagnostics,
                DirectWarnings = directAttachment.Warnings,
                ConnectorDiagnostics = connector.Diagnostics,
                ConnectorWarnings = connector.Warnings,
                ManagedDiagnostics = managed.Diagnostics
            });

            Assert.That(directAttachment.Allowed, Is.False);
            Assert.That(directAttachment.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(directAttachment.Diagnostics["protected_workspace"], Is.EqualTo("true"));
            Assert.That(directAttachment.Diagnostics["broker_only_file_context_required"], Is.EqualTo("true"));
            Assert.That(directAttachment.Diagnostics["precloud_ingress_boundary"], Is.EqualTo("unsupported"));
            Assert.That(connector.Allowed, Is.False);
            Assert.That(connector.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(connector.Diagnostics["precloud_ingress_boundary"], Is.EqualTo("unsupported"));
            Assert.That(managed.Diagnostics["broker_only_file_context_required"], Is.EqualTo("true"));
            Assert.That(managed.Diagnostics["broker_routed"], Is.EqualTo("true"));
            Assert.That(rendered, Does.Not.Contain(workspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileIngressStatusInspector_ReportsProtectedWorkspaceAsUnsupportedWithoutRawPath()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);

            var result = ProjectFileIngressStatusInspector.Inspect(layout, workspace);
            var rendered = JsonSerializer.Serialize(result);

            Assert.That(result.PreCloudBoundaryAvailable, Is.False);
            Assert.That(result.Status, Is.EqualTo(ProjectFileIngressStatusValues.Unsupported));
            Assert.That(result.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(rendered, Does.Not.Contain(workspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileIngressStatusInspector_FailsClosedForUnreadableWorkspaceRegistry()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            File.WriteAllText(ProtectedWorkspaceStore.DefaultPath(layout), "{not-json");
            var runtime = CreateRuntime(layout, new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("ingress-status-test-secret")));

            var inspected = ProjectFileIngressStatusInspector.Inspect(layout, workspace);
            var bypass = ProjectFileBypassGuard.ReportDirectAttachment(layout, workspace);
            var output = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-ingress-status", workspace }, runtime));

            Assert.That(inspected.PreCloudBoundaryAvailable, Is.False);
            Assert.That(inspected.MustBlockUnroutedContext, Is.True);
            Assert.That(inspected.Status, Is.EqualTo(ProjectFileIngressStatusValues.Unsupported));
            Assert.That(inspected.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(bypass.Allowed, Is.False);
            Assert.That(bypass.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(output.ExitCode, Is.EqualTo(1));
            Assert.That(output.Stdout, Does.Contain("precloud_ingress_boundary: unsupported"));
            Assert.That(output.Stdout, Does.Contain("project_files_protected: false"));
            Assert.That(output.Stdout, Does.Not.Contain(workspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileIngressStatusInspector_FailsClosedWhenWorkspaceRegistryCannotBeRead()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var registration = ProtectedWorkspaceStore.Protect(layout, workspace);
            var runtime = CreateRuntime(layout, new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("ingress-status-test-secret")));

            using var lockHandle = new FileStream(registration.StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var inspected = ProjectFileIngressStatusInspector.Inspect(layout, workspace);
            var bypass = ProjectFileBypassGuard.ReportDirectAttachment(layout, workspace);
            var output = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-ingress-status", workspace }, runtime));

            Assert.That(inspected.PreCloudBoundaryAvailable, Is.False);
            Assert.That(inspected.MustBlockUnroutedContext, Is.True);
            Assert.That(inspected.Status, Is.EqualTo(ProjectFileIngressStatusValues.Unsupported));
            Assert.That(inspected.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(bypass.Allowed, Is.False);
            Assert.That(bypass.Code, Is.EqualTo("project_file_ingress_unsupported"));
            Assert.That(output.ExitCode, Is.EqualTo(1));
            Assert.That(output.Stdout, Does.Contain("precloud_ingress_boundary: unsupported"));
            Assert.That(output.Stdout, Does.Contain("project_files_protected: false"));
            Assert.That(output.Stdout, Does.Not.Contain(workspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProtectedWorkspaceStore_RefusesToOverwriteCorruptedRegistry()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var initialWorkspace = Path.Combine(tempDirectory, "initial-workspace");
            var newWorkspace = Path.Combine(tempDirectory, "new-workspace");
            Directory.CreateDirectory(initialWorkspace);
            Directory.CreateDirectory(newWorkspace);
            var initialRegistration = ProtectedWorkspaceStore.Protect(layout, initialWorkspace);
            const string corruptedDocument = "{not-json";
            File.WriteAllText(initialRegistration.StorePath, corruptedDocument);
            var runtime = CreateRuntime(layout, new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("ingress-status-test-secret")));

            var registration = ProtectedWorkspaceStore.Protect(layout, newWorkspace);
            var output = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-workspace-protect", newWorkspace }, runtime));

            Assert.That(registration.Succeeded, Is.False);
            Assert.That(registration.Code, Is.EqualTo("protected_workspace_registry_unavailable"));
            Assert.That(File.ReadAllText(initialRegistration.StorePath), Is.EqualTo(corruptedDocument));
            Assert.That(output.ExitCode, Is.EqualTo(1));
            Assert.That(output.Stdout, Does.Contain("status: protected_workspace_registry_unavailable"));
            Assert.That(output.Stdout, Does.Not.Contain(newWorkspace));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileProductSmokeRunner_ProvesCompleteBrokerWorkflowRawFree()
    {
        var report = ProjectFileProductSmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("project-file-product-smoke-test-secret"));
        var rendered = string.Join(Environment.NewLine, ProjectFileProductSmokeRunner.RenderRawFree(report));

        Assert.That(report.Passed, Is.True);
        Assert.That(report.ReadSanitizedVirtualFilePassed, Is.True);
        Assert.That(report.ToolOutputSanitizedPassed, Is.True);
        Assert.That(report.ApprovedWritePassed, Is.True);
        Assert.That(report.UnsupportedFileBlockedPassed, Is.True);
        Assert.That(report.BypassBlockedPassed, Is.True);
        Assert.That(report.BrokerWorkflowPassed, Is.True);
        Assert.That(report.RawFreeAuditEvidencePassed, Is.True);
        Assert.That(rendered, Does.Contain("project_file_product_smoke: passed"));
        Assert.That(rendered, Does.Contain("project_file_broker_workflow: true"));
        Assert.That(rendered, Does.Contain("project_files_protected: false"));
        Assert.That(rendered, Does.Not.Contain("deploy.corp.example.local"));
        Assert.That(rendered, Does.Not.Contain("user1"));
        Assert.That(rendered, Does.Not.Contain("P@ssw0rd!"));
    }

    [Test]
    public void Program_ProjectFileSmokeToolOutputAndPatchDryRunCommands_ExposeRawFreeStatus()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var filePath = Path.Combine(workspace, "config.txt");
            var originalText = "Connect to 192.168.10.25\r\npassword=P@ssw0rd!";
            File.WriteAllText(filePath, originalText);
            var vault = new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("project-file-cli-test-secret"));
            var runtime = CreateRuntime(layout, vault);
            ProtectedWorkspaceStore.Protect(layout, workspace);
            var source = new ProjectFileContextBroker(
                new Sanitizer(vault),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault)
                .CreateSanitizedVirtualFile(filePath, workspace);
            var virtualFile = source.VirtualFile!;

            var smoke = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-smoke" }, runtime));
            var tool = CaptureProgramOutput(() =>
                Program.Main(new[]
                {
                    "--project-tool-output-sanitize",
                    workspace,
                    "Read C:\\Users\\user1\\secret.txt from 192.168.10.25 with password=P@ssw0rd!"
                }, runtime));
            var unmanaged = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-tool-output-unmanaged", workspace }, runtime));
            var patch = CaptureProgramOutput(() =>
                Program.Main(new[]
                {
                    "--project-patch-dry-run",
                    filePath,
                    "--protected-workspace",
                    workspace,
                    "--source-content-hash",
                    virtualFile.ContentHash,
                    "--sanitized-edit",
                    virtualFile.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal)
                }, runtime));
            File.WriteAllText(filePath, "changed outside broker");
            var stalePatch = CaptureProgramOutput(() =>
                Program.Main(new[]
                {
                    "--project-patch-dry-run",
                    filePath,
                    "--protected-workspace",
                    workspace,
                    "--source-content-hash",
                    virtualFile.ContentHash,
                    "--sanitized-edit",
                    virtualFile.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal)
                }, runtime));
            File.WriteAllText(filePath, originalText);
            var apply = CaptureProgramOutput(() =>
                Program.Main(new[]
                {
                    "--project-patch-apply",
                    filePath,
                    "--protected-workspace",
                    workspace,
                    "--source-content-hash",
                    virtualFile.ContentHash,
                    "--sanitized-edit",
                    virtualFile.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal),
                    "--approve"
                }, runtime));
            File.WriteAllText(filePath, originalText);
            var bypass = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-attachment-bypass-status", workspace }, runtime));
            var ingress = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-ingress-status", workspace }, runtime));
            var productSmoke = CaptureProgramOutput(() =>
                Program.Main(new[] { "--project-file-product-smoke" }, runtime));

            Assert.That(smoke.ExitCode, Is.EqualTo(0));
            Assert.That(smoke.Stdout, Does.Contain("project_file_smoke: passed"));
            Assert.That(tool.ExitCode, Is.EqualTo(0));
            Assert.That(tool.Stdout, Does.Contain("status: managed_tool_output_sanitized"));
            Assert.That(tool.Stdout, Does.Contain("sanitized_tool_output:"));
            Assert.That(tool.Stdout, Does.Not.Contain("user1"));
            Assert.That(tool.Stdout, Does.Not.Contain("192.168.10.25"));
            Assert.That(tool.Stdout, Does.Not.Contain("P@ssw0rd!"));
            Assert.That(unmanaged.ExitCode, Is.EqualTo(1));
            Assert.That(unmanaged.Stdout, Does.Contain("status: unmanaged_tool_output_unprotected"));
            Assert.That(unmanaged.Stdout, Does.Not.Contain(workspace));
            Assert.That(patch.ExitCode, Is.EqualTo(0));
            Assert.That(patch.Stdout, Does.Contain("status: restore_aware_patch_preview_ready"));
            Assert.That(patch.Stdout, Does.Contain("local_sensitive: true"));
            Assert.That(patch.Stdout, Does.Contain("Route to 192.168.10.25"));
            Assert.That(apply.ExitCode, Is.EqualTo(0));
            Assert.That(apply.Stdout, Does.Contain("status: project_patch_applied"));
            Assert.That(apply.Stdout, Does.Contain("written: true"));
            Assert.That(apply.Stdout, Does.Not.Contain("192.168.10.25"));
            Assert.That(apply.Stdout, Does.Not.Contain("P@ssw0rd!"));
            Assert.That(bypass.ExitCode, Is.EqualTo(1));
            Assert.That(bypass.Stdout, Does.Contain("status: project_file_ingress_unsupported"));
            Assert.That(bypass.Stdout, Does.Not.Contain(workspace));
            Assert.That(ingress.ExitCode, Is.EqualTo(1));
            Assert.That(ingress.Stdout, Does.Contain("status: project_file_ingress_unsupported"));
            Assert.That(ingress.Stdout, Does.Contain("precloud_ingress_boundary: unsupported"));
            Assert.That(ingress.Stdout, Does.Not.Contain(workspace));
            Assert.That(productSmoke.ExitCode, Is.EqualTo(0));
            Assert.That(productSmoke.Stdout, Does.Contain("project_file_product_smoke: passed"));
            Assert.That(File.ReadAllText(filePath), Does.Not.Contain("Route to"));
            Assert.That(stalePatch.ExitCode, Is.EqualTo(1));
            Assert.That(stalePatch.Stdout, Does.Contain("status: source_version_mismatch"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileSelectionGuard_RejectsDirectoryLinkEscapingWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Pass("Windows directory-link containment is Windows-specific.");
        }

        var tempDirectory = CreateTempDirectory();

        try
        {
            var workspace = Path.Combine(tempDirectory, "workspace");
            var outside = Path.Combine(tempDirectory, "outside");
            var link = Path.Combine(workspace, "linked");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "config.txt"), "Connect to 192.168.10.25");
            Directory.CreateSymbolicLink(link, outside);

            var result = ProjectFileSelectionGuard.CheckWorkspaceContainment(
                Path.Combine(link, "config.txt"),
                workspace);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("outside_workspace"));
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Pass("Creating directory links requires privileges on this Windows host.");
        }
        catch (IOException) when (!Directory.Exists(Path.Combine(tempDirectory, "workspace", "linked")))
        {
            Assert.Pass("Creating directory links requires privileges on this Windows host.");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static CliRuntime CreateRuntime(DefaultStorageLayout layout, InMemoryHmacMappingVault? vault = null)
    {
        var mappingVault = vault ?? new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("project-file-test-secret"));
        return new CliRuntime(
            _ => new Sanitizer(mappingVault),
            () => Sanitizer.LoadProductionPolicy(layout),
            _ => new Sanitizer(mappingVault),
            () => layout,
            _ => new LocalRestoreWorkflow(
                new LocalRestorer(mappingVault),
                new FileAuditSink(Path.Combine(layout.RootDirectory, "audit"))));
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

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-project-file-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
