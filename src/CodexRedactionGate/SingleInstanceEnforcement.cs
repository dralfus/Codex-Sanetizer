using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Collections.Generic;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Callback delegate for user notifications
/// </summary>
/// <param name="title">Notification title</param>
/// <param name="message">Notification message</param>
/// <param name="includeDiagnosticsLink">Whether to include diagnostics link</param>
public delegate void UserNotificationCallback(string title, string message, bool includeDiagnosticsLink);

/// <summary>
/// Single instance enforcement for resident Code Sanitizer tray app
/// Ensures only one hook-owning resident instance per user
/// </summary>
public sealed class SingleInstanceEnforcement : IDisposable
{
    private static readonly object OwnedNamesLock = new();
    private static readonly HashSet<string> ProcessOwnedNames = new(StringComparer.Ordinal);
    private readonly Mutex _mutex;
    private readonly bool _isFirstInstance;
    private readonly bool _ownsMutex;
    private readonly string _mutexName;
    private int _disposed;

    /// <summary>
    /// Creates a new single instance enforcement
    /// </summary>
    /// <param name="instanceId">Unique instance identifier</param>
    public SingleInstanceEnforcement(string instanceId)
        : this(instanceId, useGlobalNamespace: false)
    {
    }

    public SingleInstanceEnforcement(string instanceId, bool useGlobalNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (useGlobalNamespace && !IsElevated())
        {
            throw new UnauthorizedAccessException("global_mutex_requires_elevation");
        }

        _mutexName = BuildMutexName(instanceId, useGlobalNamespace);
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out var createdNew);
        if (createdNew)
        {
            _ownsMutex = true;
            _isFirstInstance = true;
            RegisterProcessOwnership(_mutexName);
            return;
        }

        if (ProcessOwns(_mutexName))
        {
            _ownsMutex = false;
            _isFirstInstance = false;
            return;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        _isFirstInstance = _ownsMutex;
        if (_ownsMutex)
        {
            RegisterProcessOwnership(_mutexName);
        }
    }

    /// <summary>
    /// Checks if this is the first instance
    /// </summary>
    public bool IsFirstInstance => _isFirstInstance;

    /// <summary>
    /// Check if another instance is already running
    /// </summary>
    public static bool IsAnotherInstanceRunning(string instanceId)
    {
        return IsAnotherInstanceRunning(instanceId, useGlobalNamespace: false);
    }

    public static bool IsAnotherInstanceRunning(string instanceId, bool useGlobalNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (useGlobalNamespace && !IsElevated())
        {
            return true;
        }

        var mutexName = BuildMutexName(instanceId, useGlobalNamespace);
        
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            // Try to wait on the mutex without blocking
            // Returns true if we acquired it (meaning no other instance owns it)
            // Returns false if we didn't acquire it (meaning another instance owns it)
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
                return !acquired;
            }
            catch (AbandonedMutexException)
            {
                // The prior process exited without releasing the mutex. Windows
                // transferred ownership to this caller, so the slot is available.
                acquired = true;
                return false;
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Mutex doesn't exist - no other instance
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Another instance exists but we don't have access
            return true;
        }
    }

    /// <summary>
    /// Activates the existing tray instance through its per-user registered window.
    /// The handle is accepted only after the matching per-user mutex was opened. Win32 foreground rules
    /// may still deny activation; callers must treat <c>false</c> as a visible,
    /// raw-free notification case. This mechanism is session-local: a global
    /// mutex prevents duplicate hook ownership across sessions but does not grant
    /// cross-session UI activation rights.
    /// </summary>
    /// <param name="instanceId">Instance identifier</param>
    /// <param name="notificationCallback">Optional callback to show user notification</param>
    /// <returns>True only when the existing instance's activation window was foregrounded.</returns>
    public static bool ActivateExistingInstance(string instanceId, UserNotificationCallback? notificationCallback = null)
    {
        return ActivateExistingInstance(instanceId, useGlobalNamespace: false, notificationCallback);
    }

    public static bool ActivateExistingInstance(
        string instanceId,
        bool useGlobalNamespace,
        UserNotificationCallback? notificationCallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var mutexName = BuildMutexName(instanceId, useGlobalNamespace);

        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            if (!TrayActivationWindowStore.Default.TryRead(instanceId, out var window))
            {
                notificationCallback?.Invoke(
                    AppStrings.Get("ProductName"),
                    AppStrings.Get("AlreadyRunning"),
                    includeDiagnosticsLink: false);
                return false;
            }

            if (window != IntPtr.Zero && NativeMethods.IsWindow(window))
            {
                NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
                if (NativeMethods.SetForegroundWindow(window))
                {
                    return true;
                }
            }

            TrayActivationWindowStore.Default.Clear(instanceId);

            notificationCallback?.Invoke(
                AppStrings.Get("ProductName"),
                AppStrings.Get("AlreadyRunning"),
                includeDiagnosticsLink: false);
            return false;
        }
        catch
        {
            notificationCallback?.Invoke(
                AppStrings.Get("ProductName"),
                AppStrings.Get("AlreadyRunning"),
                includeDiagnosticsLink: false);

            return false;
        }
    }

    internal static bool RegisterActivationWindow(string instanceId, IntPtr windowHandle)
    {
        return TrayActivationWindowStore.Default.TryStore(instanceId, windowHandle);
    }

    internal static void ClearActivationWindow(string instanceId)
    {
        TrayActivationWindowStore.Default.Clear(instanceId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The owner thread has already released the mutex.
            }
            finally
            {
                UnregisterProcessOwnership(_mutexName);
            }
        }

        _mutex.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static string BuildMutexName(string instanceId, bool useGlobalNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var name = $"CodexRedactionGate_{instanceId}";
        return useGlobalNamespace ? $"Global\\{name}" : name;
    }

    internal static bool CanUseGlobalMutex => IsElevated();

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ProcessOwns(string mutexName)
    {
        lock (OwnedNamesLock)
        {
            return ProcessOwnedNames.Contains(mutexName);
        }
    }

    private static void RegisterProcessOwnership(string mutexName)
    {
        lock (OwnedNamesLock)
        {
            ProcessOwnedNames.Add(mutexName);
        }
    }

    private static void UnregisterProcessOwnership(string mutexName)
    {
        lock (OwnedNamesLock)
        {
            ProcessOwnedNames.Remove(mutexName);
        }
    }

    private static class NativeMethods
    {
        internal const int SwRestore = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);
    }
}
