using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace CodexRedactionGate;

internal sealed class TrayActivationWindowStore
{
    private const string RegistryPath = @"Software\CodexRedactionGate\Runtime\ActivationWindows";

    public static TrayActivationWindowStore Default { get; } = new(RegistryPath);

    private readonly string _registryPath;

    internal TrayActivationWindowStore(string registryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        _registryPath = registryPath;
    }

    public bool TryStore(string instanceId, IntPtr windowHandle)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // HKCU is isolated by the Windows user SID and inherits that user's ACL.
            using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
            key?.SetValue(instanceId, windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
            return key is not null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ArgumentException)
        {
            return false;
        }
    }

    public bool TryRead(string instanceId, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
            if (key?.GetValue(instanceId) is not string value
                || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawHandle)
                || rawHandle == 0)
            {
                return false;
            }

            windowHandle = new IntPtr(rawHandle);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ArgumentException)
        {
            return false;
        }
    }

    public void Clear(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
            key?.DeleteValue(instanceId, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ArgumentException)
        {
        }
    }
}
