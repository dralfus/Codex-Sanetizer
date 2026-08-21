using System;
using System.IO;
using System.Threading;
using CodexRedactionGate;

internal sealed class WorkflowTestDirectory : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    internal DefaultStorageLayout Layout => DefaultStorageLayout.Create(_path);

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}

internal sealed class WorkflowStartContender : IDisposable
{
    private readonly ManualResetEventSlim _gateEntered = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private Thread? _thread;
    private int _armed;

    internal OperationalActionStartResult Result { get; private set; } = default!;

    internal bool TerminalObserved { get; private set; }

    internal void ObserveGate(string operation)
    {
        if (Volatile.Read(ref _armed) != 0 && operation == "start_action")
        {
            _gateEntered.Set();
        }
    }

    internal void ObserveSnapshot(IResidentProtectionWorkflowPort runtime)
    {
        TerminalObserved |= runtime.OperationalAction.Status == "succeeded";
    }

    internal void StartAndWaitUntilBlocked(
        IResidentProtectionWorkflowPort runtime,
        string actionKind)
    {
        Volatile.Write(ref _armed, 1);
        _thread = new Thread(() =>
        {
            Result = runtime.StartAction(new ResidentWorkflowActionRequest(
                actionKind, "starting", false, "wait_for_result"));
            _finished.Set();
        });
        _thread.Start();
        if (!_gateEntered.Wait(TimeSpan.FromSeconds(1)))
        {
            throw new TimeoutException("The competing workflow did not reach the resident gate.");
        }

        if (_finished.IsSet)
        {
            throw new InvalidOperationException("The competing workflow crossed an admitted transaction.");
        }
    }

    internal void WaitForCompletion()
    {
        if (!_finished.Wait(TimeSpan.FromSeconds(1)))
        {
            throw new TimeoutException("The competing workflow did not complete after the resident gate opened.");
        }

        _thread!.Join();
    }

    public void Dispose()
    {
        _gateEntered.Dispose();
        _finished.Dispose();
    }
}
