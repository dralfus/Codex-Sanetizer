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
            File.WriteAllText(filePath, "Connect to 192.168.10.25\r\npassword=P@ssw0rd!");
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

    private static CliRuntime CreateRuntime(DefaultStorageLayout layout)
    {
        return new CliRuntime(
            _ => TestSanitizers.Create(),
            () => Sanitizer.LoadProductionPolicy(layout),
            _ => TestSanitizers.Create(),
            () => layout,
            LocalRestoreWorkflow.CreateProduction);
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
