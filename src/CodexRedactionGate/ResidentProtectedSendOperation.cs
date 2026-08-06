using System;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Owns one suppressed protected Send until it reaches a terminal result.
/// </summary>
internal sealed class ResidentProtectedSendOperation : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _lifecycleGate = new();
    private int _completed;
    private bool _disposed;

    public ResidentProtectedSendOperation(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitTargetIdentity? target)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        RuntimeSet = runtimeSet ?? throw new ArgumentNullException(nameof(runtimeSet));
        Target = target;
    }

    public ProtectionSnapshot Snapshot { get; }

    public NativeSubmitRuntimeSet RuntimeSet { get; }

    public NativeSubmitTargetIdentity? Target { get; }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool IsCancelled
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _cancellation.IsCancellationRequested;
            }
        }
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

    public void Cancel()
    {
        lock (_lifecycleGate)
        {
            if (!_disposed)
            {
                _cancellation.Cancel();
            }
        }
    }

    public bool TryComplete()
    {
        return Interlocked.Exchange(ref _completed, 1) == 0;
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
