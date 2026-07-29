using System;
using Microsoft.Win32;

namespace CodexRedactionGate;

internal sealed record SingleInstanceNotificationSettings(bool Enabled, string Type)
{
    private const string RegistryPath = @"Software\CodexRedactionGate\SingleInstance";

    public static SingleInstanceNotificationSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return FromRegistryValues(
                key?.GetValue("DisableNotification"),
                key?.GetValue("NotificationType") as string);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FromRegistryValues(disableNotification: null, notificationType: null);
        }
    }

    internal static SingleInstanceNotificationSettings FromRegistryValues(
        object? disableNotification,
        string? notificationType)
    {
        if (disableNotification is int disabled && disabled != 0)
        {
            return new SingleInstanceNotificationSettings(false, "none");
        }

        return new SingleInstanceNotificationSettings(true, NormalizeType(notificationType));
    }

    internal static string NormalizeType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            // Keep legacy deployments non-modal as required by the tray contract.
            "messagebox" => "balloon",
            "toast" => "toast",
            "balloon" => "balloon",
            _ => "balloon"
        };
    }
}
