using System;
using Microsoft.Win32;

namespace CodexRedactionGate;

public sealed record AutostartState(
    bool Enabled,
    string Code,
    string? ConfiguredCommandLine,
    string ExpectedCommandLine,
    string RegistryValueName);

public interface IUserStartupRegistration
{
    string? Read(string valueName);

    void Write(string valueName, string commandLine);

    void Delete(string valueName);
}

public static class AutostartManager
{
    public const string RegistryValueName = "CodexRedactionGate";

    public static AutostartState Show(IUserStartupRegistration registry, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        var configured = registry.Read(RegistryValueName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return CreateState(false, "autostart_disabled", null, commandLine);
        }

        return string.Equals(configured, commandLine, StringComparison.Ordinal)
            ? CreateState(true, "autostart_enabled", configured, commandLine)
            : CreateState(false, "autostart_mismatch", configured, commandLine);
    }

    public static AutostartState Enable(IUserStartupRegistration registry, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        registry.Write(RegistryValueName, commandLine);
        return Show(registry, commandLine);
    }

    public static AutostartState Disable(IUserStartupRegistration registry, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        registry.Delete(RegistryValueName);
        return Show(registry, commandLine);
    }

    private static AutostartState CreateState(
        bool enabled,
        string code,
        string? configuredCommandLine,
        string expectedCommandLine)
    {
        return new AutostartState(
            Enabled: enabled,
            Code: code,
            ConfiguredCommandLine: configuredCommandLine,
            ExpectedCommandLine: expectedCommandLine,
            RegistryValueName: RegistryValueName);
    }
}

public sealed class WindowsRunStartupRegistration : IUserStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string valueName, string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(valueName, commandLine, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
