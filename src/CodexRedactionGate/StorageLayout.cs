using System;
using System.IO;

namespace CodexRedactionGate;

public sealed record DefaultStorageLayout(
    string RootDirectory,
    string PolicyDirectory,
    string VaultDirectory,
    string AuditDirectory,
    string SettingsDirectory)
{
    public static DefaultStorageLayout CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "CodexRedactionGate");
        return Create(root);
    }

    public static DefaultStorageLayout Create(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);
        return new DefaultStorageLayout(
            RootDirectory: root,
            PolicyDirectory: Path.Combine(root, "policy"),
            VaultDirectory: Path.Combine(root, "vault"),
            AuditDirectory: Path.Combine(root, "audit"),
            SettingsDirectory: Path.Combine(root, "settings"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(PolicyDirectory);
        Directory.CreateDirectory(VaultDirectory);
        Directory.CreateDirectory(AuditDirectory);
        Directory.CreateDirectory(SettingsDirectory);
    }
}
