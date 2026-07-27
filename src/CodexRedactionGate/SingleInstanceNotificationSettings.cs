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
            if (key?.GetValue("DisableNotification") is int disabled && disabled != 0)
            {
                return new SingleInstanceNotificationSettings(false, "none");
            }

            return new SingleInstanceNotificationSettings(true, NormalizeType(key?.GetValue("NotificationType") as string));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new SingleInstanceNotificationSettings(true, "balloon");
        }
    }

    internal static string NormalizeType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "messagebox" => "messagebox",
            "toast" => "toast",
            "balloon" => "balloon",
            _ => "balloon"
        };
    }
}
