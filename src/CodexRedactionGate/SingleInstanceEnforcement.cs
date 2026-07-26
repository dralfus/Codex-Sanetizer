using System;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Single instance enforcement for resident Code Sanitizer tray app
/// Ensures only one hook-owning resident instance per user
/// </summary>
public sealed class SingleInstanceEnforcement : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _mutexCreated;
    private readonly string _mutexName;

    /// <summary>
    /// Creates a new single instance enforcement
    /// </summary>
    /// <param name="instanceId">Unique instance identifier</param>
    public SingleInstanceEnforcement(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        // Use local mutex name (no Global\ prefix - requires elevation on Windows)
        _mutexName = $"CodexRedactionGate_{instanceId}";
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out _mutexCreated);
    }

    /// <summary>
    /// Checks if this is the first instance
    /// </summary>
    public bool IsFirstInstance => _mutexCreated;

    /// <summary>
    /// Check if another instance is already running
    /// </summary>
    public static bool IsAnotherInstanceRunning(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        // Use local mutex name (no Global\ prefix - requires elevation on Windows)
        var mutexName = $"CodexRedactionGate_{instanceId}";
        
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            // Try to wait on the mutex without blocking
            // Returns true if we acquired it (meaning no other instance owns it)
            // Returns false if we didn't acquire it (meaning another instance owns it)
            var acquired = mutex.WaitOne(TimeSpan.Zero);
            return !acquired; // If we didn't acquire, another instance owns it
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
    /// Activates the existing instance
    /// </summary>
    public static bool ActivateExistingInstance(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        // Use local mutex name
        var mutexName = $"CodexRedactionGate_{instanceId}";

        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Close();
        GC.SuppressFinalize(this);
    }

    ~SingleInstanceEnforcement()
    {
        Dispose();
    }
}
