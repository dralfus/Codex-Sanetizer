using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace CodexRedactionGate;

internal sealed class ReferenceOnlyInputCapability
{
    // Object identity is the authorization check; entropy prevents a
    // capability-like marker from being reconstructed from persistent data.
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public bool IsValid => _nonce.Length == 32;
}

internal sealed record ReferenceOnlyInputTarget(
    uint ProcessId,
    IntPtr RootWindow,
    int ManagedUiThreadId,
    uint? WindowsUiThreadId)
{
    internal static ReferenceOnlyInputTarget ForCurrentProcessForTest(IntPtr rootWindow)
    {
        if (rootWindow == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rootWindow));
        }

        return new ReferenceOnlyInputTarget(
            (uint)Environment.ProcessId,
            rootWindow,
            Environment.CurrentManagedThreadId,
            WindowsUiThreadId: null);
    }

    internal static bool TryCreateForCurrentProcessWindow(
        IntPtr rootWindow,
        out ReferenceOnlyInputTarget? target)
    {
        target = null;
        if (!OperatingSystem.IsWindows() || rootWindow == IntPtr.Zero)
        {
            return false;
        }

        var windowThreadId = NativeMethods.GetWindowThreadProcessId(rootWindow, out var processId);
        if (processId != (uint)Environment.ProcessId
            || windowThreadId == 0
            || windowThreadId != NativeMethods.GetCurrentThreadId())
        {
            return false;
        }

        target = new ReferenceOnlyInputTarget(
            processId,
            rootWindow,
            Environment.CurrentManagedThreadId,
            windowThreadId);
        return true;
    }

    public bool Matches(NativeKeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return Matches(gesture.TargetWindow, gesture.TargetProcessId);
    }

    public bool Matches(NativePointerGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return Matches(gesture.TargetWindow, gesture.TargetProcessId);
    }

    public bool IsCurrentUiThread()
        => WindowsUiThreadId is { } windowsUiThreadId
            ? windowsUiThreadId == NativeMethods.GetCurrentThreadId()
            : ManagedUiThreadId == Environment.CurrentManagedThreadId;

    private bool Matches(IntPtr targetWindow, uint targetProcessId)
    {
        return targetProcessId == ProcessId
            && targetWindow == RootWindow
            && IsCurrentUiThread();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}

internal sealed record ReferenceOnlyInputDispatchResult(
    bool Accepted,
    bool SuppressOriginalInput,
    string Status)
{
    public static ReferenceOnlyInputDispatchResult Unavailable { get; } = new(
        Accepted: false,
        SuppressOriginalInput: false,
        OsInteractionStatusIds.ReferenceSourceUnavailable);
}

/// <summary>
/// A non-persistable acceptance input capability for the local reference composer.
/// It has no profile selection API and can never name a Codex or ChatGPT target.
/// </summary>
internal sealed class ReferenceOnlyInputSource : IDisposable
{
    internal const string ProfileId = "reference-composer";
    private static readonly TimeSpan DefaultAcceptanceLifetime = TimeSpan.FromMinutes(5);

    private readonly ReferenceOnlyInputCapability _capability = new();
    private readonly ReferenceOnlyInputTarget _target;
    private readonly Func<ReferenceOnlyInputCapability, NativeKeyGesture, ReferenceOnlyInputDispatchResult> _keyboardDispatcher;
    private readonly Func<ReferenceOnlyInputCapability, NativePointerGesture, ReferenceOnlyInputDispatchResult> _pointerDispatcher;
    private readonly Action<ReferenceOnlyInputCapability> _revoke;
    private readonly long _expiresAtTimestamp;
    private int _disposed;

    private ReferenceOnlyInputSource(
        ReferenceOnlyInputTarget target,
        Func<ReferenceOnlyInputCapability, NativeKeyGesture, ReferenceOnlyInputDispatchResult> keyboardDispatcher,
        Func<ReferenceOnlyInputCapability, NativePointerGesture, ReferenceOnlyInputDispatchResult> pointerDispatcher,
        Action<ReferenceOnlyInputCapability> revoke,
        TimeSpan acceptanceLifetime)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _keyboardDispatcher = keyboardDispatcher ?? throw new ArgumentNullException(nameof(keyboardDispatcher));
        _pointerDispatcher = pointerDispatcher ?? throw new ArgumentNullException(nameof(pointerDispatcher));
        _revoke = revoke ?? throw new ArgumentNullException(nameof(revoke));
        if (acceptanceLifetime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptanceLifetime));
        }

        _expiresAtTimestamp = checked(
            Stopwatch.GetTimestamp()
            + (long)(acceptanceLifetime.TotalSeconds * Stopwatch.Frequency));
    }

    internal ReferenceOnlyInputTarget Target => _target;

    internal ReferenceOnlyInputCapability Capability => _capability;

    internal static SubmitKeyBinding SubmitBinding { get; } = SubmitKeyBinding.Parse("Enter").Binding!;

    public static bool IsReservedProfileId(string? profileId)
        => string.Equals(profileId, ProfileId, StringComparison.Ordinal);

    internal static bool TryCreateForAcceptance(
        string profileId,
        ReferenceOnlyInputTarget target,
        Func<ReferenceOnlyInputCapability, NativeKeyGesture, ReferenceOnlyInputDispatchResult> keyboardDispatcher,
        Action<ReferenceOnlyInputCapability> revoke,
        out ReferenceOnlyInputSource? source)
    {
        return TryCreateForAcceptance(
            profileId,
            target,
            keyboardDispatcher,
            (_, _) => ReferenceOnlyInputDispatchResult.Unavailable,
            revoke,
            DefaultAcceptanceLifetime,
            out source);
    }

    internal static bool TryCreateForAcceptance(
        string profileId,
        ReferenceOnlyInputTarget target,
        Func<ReferenceOnlyInputCapability, NativeKeyGesture, ReferenceOnlyInputDispatchResult> keyboardDispatcher,
        Action<ReferenceOnlyInputCapability> revoke,
        TimeSpan acceptanceLifetime,
        out ReferenceOnlyInputSource? source)
    {
        return TryCreateForAcceptance(
            profileId,
            target,
            keyboardDispatcher,
            (_, _) => ReferenceOnlyInputDispatchResult.Unavailable,
            revoke,
            acceptanceLifetime,
            out source);
    }

    internal static bool TryCreateForAcceptance(
        string profileId,
        ReferenceOnlyInputTarget target,
        Func<ReferenceOnlyInputCapability, NativeKeyGesture, ReferenceOnlyInputDispatchResult> keyboardDispatcher,
        Func<ReferenceOnlyInputCapability, NativePointerGesture, ReferenceOnlyInputDispatchResult> pointerDispatcher,
        Action<ReferenceOnlyInputCapability> revoke,
        TimeSpan acceptanceLifetime,
        out ReferenceOnlyInputSource? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(target);

        if (!IsReservedProfileId(profileId)
            || target.ProcessId != (uint)Environment.ProcessId
            || target.RootWindow == IntPtr.Zero
            || !target.IsCurrentUiThread())
        {
            source = null;
            return false;
        }

        source = new ReferenceOnlyInputSource(target, keyboardDispatcher, pointerDispatcher, revoke, acceptanceLifetime);
        return true;
    }

    internal ReferenceOnlyInputDispatchResult DispatchKeyboard(NativeKeyGesture gesture)
    {
        return CanDispatch(gesture)
            ? _keyboardDispatcher(_capability, gesture)
            : ReferenceOnlyInputDispatchResult.Unavailable;
    }

    internal ReferenceOnlyInputDispatchResult DispatchPointer(NativePointerGesture gesture)
    {
        return CanDispatch(gesture)
            ? _pointerDispatcher(_capability, gesture)
            : ReferenceOnlyInputDispatchResult.Unavailable;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _revoke(_capability);
        }
    }

    private bool CanDispatch(NativeKeyGesture gesture)
        => IsActive() && _target.Matches(gesture);

    private bool CanDispatch(NativePointerGesture gesture)
        => IsActive() && _target.Matches(gesture);

    private bool IsActive()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_capability.IsValid)
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() < _expiresAtTimestamp)
        {
            return true;
        }

        Dispose();
        return false;
    }
}
