using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexRedactionGate;

/// <summary>
/// Owns one suppressed protected Send until it reaches a terminal result.
/// </summary>
internal sealed class ResidentProtectedSendOperation : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action? _cancelSideEffects;
    private IReadOnlyList<ProtectedSendTraceEntry> _trace = Array.Empty<ProtectedSendTraceEntry>();
    private int _completed;
    private int _cancelled;
    private int _executionThreadId;
    private bool _disposed;

    public ResidentProtectedSendOperation(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitTargetIdentity? target,
        Action? cancelSideEffects = null)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        RuntimeSet = runtimeSet ?? throw new ArgumentNullException(nameof(runtimeSet));
        Target = target;
        _cancelSideEffects = cancelSideEffects;
        AttemptId = snapshot.State.ProtectedSendAttemptId == long.MaxValue
            ? 1
            : snapshot.State.ProtectedSendAttemptId + 1;
        TargetFingerprint = ProtectedSendTrace.TargetFingerprint(
            target,
            target?.ProfileId ?? runtimeSet.Runtimes.FirstOrDefault()?.Profile.ProfileId);
        StartedAtTimestamp = Stopwatch.GetTimestamp();
    }

    public ProtectionSnapshot Snapshot { get; }

    public NativeSubmitRuntimeSet RuntimeSet { get; }

    public NativeSubmitTargetIdentity? Target { get; }

    public long AttemptId { get; }

    public string TargetFingerprint { get; }

    public long StartedAtTimestamp { get; }

    public IReadOnlyList<ProtectedSendTraceEntry> Trace
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _trace.ToArray();
            }
        }
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool IsCancelled
    {
        get => Volatile.Read(ref _cancelled) != 0;
    }

    public bool CanContinue(ProtectionSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        lock (_lifecycleGate)
        {
            return CanContinueUnderLock(current);
        }
    }

    public IDisposable? TryAcquireSideEffect(ProtectionSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Monitor.Enter(_lifecycleGate);
        if (!CanContinueUnderLock(current))
        {
            Monitor.Exit(_lifecycleGate);
            return null;
        }

        return new LifecycleLease(_lifecycleGate);
    }

    public void MarkExecutionStarted()
    {
        Volatile.Write(ref _executionThreadId, Environment.CurrentManagedThreadId);
    }

    public bool TryAppendTraceTransaction(
        string stage,
        string resultCode,
        int durationMilliseconds,
        Func<IReadOnlyList<ProtectedSendTraceEntry>, bool> tryPublish,
        out IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        ArgumentNullException.ThrowIfNull(tryPublish);

        lock (_lifecycleGate)
        {
            if (_disposed
                || Volatile.Read(ref _completed) != 0
                || Volatile.Read(ref _cancelled) != 0
                || !ProtectedSendTrace.TryAppend(
                    _trace,
                    AttemptId,
                    Snapshot.Generation,
                    TargetFingerprint,
                    stage,
                    resultCode,
                    durationMilliseconds,
                    out var updated))
            {
                trace = _trace;
                return false;
            }

            try
            {
                if (!tryPublish(updated))
                {
                    trace = _trace;
                    return false;
                }
            }
            catch
            {
                trace = _trace;
                return false;
            }

            _trace = updated;
            trace = _trace;
            return true;
        }
    }

    public bool TryEnsureTerminalBlockedTrace(out IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        lock (_lifecycleGate)
        {
            if (_disposed || Volatile.Read(ref _completed) != 0)
            {
                trace = _trace;
                return false;
            }

            if (!TryCreateTerminalBlockedTraceUnderLock(out var updated))
            {
                trace = _trace;
                return false;
            }

            _trace = updated;
            trace = _trace;
            return true;
        }
    }

    public bool TryEnsureTerminalBlockedTraceTransaction(
        Func<IReadOnlyList<ProtectedSendTraceEntry>, bool> tryPublish,
        out IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        ArgumentNullException.ThrowIfNull(tryPublish);

        lock (_lifecycleGate)
        {
            if (_disposed || Volatile.Read(ref _completed) != 0)
            {
                trace = _trace;
                return false;
            }

            if (!TryCreateTerminalBlockedTraceUnderLock(out var updated))
            {
                trace = _trace;
                return false;
            }

            try
            {
                if (!tryPublish(updated))
                {
                    trace = _trace;
                    return false;
                }
            }
            catch
            {
                trace = _trace;
                return false;
            }

            _trace = updated;
            trace = _trace;
            return true;
        }
    }

    public bool WaitForCompletion(TimeSpan timeout)
    {
        if (Volatile.Read(ref _executionThreadId) == Environment.CurrentManagedThreadId)
        {
            return false;
        }

        return _completion.Task.Wait(timeout);
    }

    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _cancelSideEffects?.Invoke();
            }
            catch
            {
                // Cancellation remains fail-closed even if a UI side-effect cannot be closed.
            }
        }
    }

    public bool TryComplete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return false;
        }

        _completion.TrySetResult(true);
        return true;
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Dispose();
        }
    }

    private bool CanContinueUnderLock(ProtectionSnapshot current)
    {
        return Volatile.Read(ref _completed) == 0
            && Volatile.Read(ref _cancelled) == 0
            && !_cancellation.IsCancellationRequested
            && current.State.Enabled
            && current.HookReady
            && current.Generation == Snapshot.Generation
            && ReferenceEquals(current.RuntimeSet, RuntimeSet)
            && string.Equals(
                current.State.LocalProtectionStatus,
                LocalProtectionRecovery.ReadyCode,
                StringComparison.Ordinal);
    }

    private bool TryCreateTerminalBlockedTraceUnderLock(out IReadOnlyList<ProtectedSendTraceEntry> updated)
    {
        updated = _trace;
        if (updated.Count == 0
            && !ProtectedSendTrace.TryAppend(
                updated,
                AttemptId,
                Snapshot.Generation,
                TargetFingerprint,
                "send_detected",
                "checking_prompt",
                0,
                out updated))
        {
            return false;
        }

        return updated[^1].Stage is "terminal_blocked" or "sent_safely"
            || ProtectedSendTrace.TryAppend(
                updated,
                AttemptId,
                Snapshot.Generation,
                TargetFingerprint,
                "terminal_blocked",
                OsInteractionStatusIds.FailedClosed,
                0,
                out updated);
    }

    private sealed class LifecycleLease : IDisposable
    {
        private readonly object _gate;
        private int _disposed;

        public LifecycleLease(object gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Monitor.Exit(_gate);
            }
        }
    }
}
