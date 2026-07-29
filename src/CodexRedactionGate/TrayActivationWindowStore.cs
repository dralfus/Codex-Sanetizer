using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace CodexRedactionGate;

/// <summary>
/// Best-effort, per-user storage for the resident tray activation window handle.
/// The handle is written only after the window is created and is removed when the
/// owning application context is disposed. HKCU keeps the value scoped to the
/// current Windows user; callers must still validate it with <c>IsWindow</c>
/// because a handle can become stale between read and activation.
/// </summary>
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

    /// <summary>
    /// Registers a non-zero handle for the first resident instance. This is
    /// intentionally best-effort: failure must fall back to the raw-free
    /// second-launch notification rather than affect protection ownership.
    /// </summary>
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

    /// <summary>
    /// Reads the last registered handle for the current user. The returned value
    /// is untrusted until the caller validates it against the live Win32 window.
    /// </summary>
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

    /// <summary>
    /// Removes the registration during normal shutdown or after stale-handle
    /// detection. Concurrent clear operations are harmless because deletion is
    /// idempotent.
    /// </summary>
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
