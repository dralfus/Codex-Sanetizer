using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexRedactionGate;

/// <summary>
/// The resident-owned state of one operational prerequisite. Values are
/// intentionally limited to raw-free tokens so the state can be rendered and
/// journaled without inspecting prompt or window content.
/// </summary>
public sealed record OperationalActionState(
    string ActionKind = "none",
    string Status = "idle",
    string Stage = "none",
    string InputMode = "automatic",
    string OutcomeCode = "none",
    string NextAction = "none",
    bool CanCancel = false,
    long AttemptId = 0,
    string CorrelationId = "none",
    long ElapsedMilliseconds = 0)
{
    public static OperationalActionState Idle { get; } = new();
}

public sealed record OperationalActionStartResult(
    bool Started,
    string Code,
    long AttemptId,
    string CorrelationId);

public sealed record OperationalActionJournalEntry(
    string CorrelationId,
    string ActionKind,
    string Transition,
    string Stage,
    string OutcomeCode,
    long ElapsedMilliseconds,
    int AttemptNumber,
    string BuildVersion);

/// <summary>
/// Bounded local journal for operational lifecycle transitions. The journal
/// accepts typed safe fields only; it has no API that can receive prompt text.
/// </summary>
internal sealed class OperationalActionJournal
{
    private const string FileName = "operational-actions.jsonl";
    private readonly DefaultStorageLayout _layout;
    private readonly int _maxRecords;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public OperationalActionJournal(DefaultStorageLayout layout, int maxRecords = 200)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        if (maxRecords <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecords));
        }

        _maxRecords = maxRecords;
    }

    internal string Path => System.IO.Path.Combine(_layout.SettingsDirectory, FileName);

    public bool TryAppend(OperationalActionJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsSafe(entry))
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                _layout.EnsureDirectories();
                var records = ReadCore().ToList();
                records.Add(entry);
                if (records.Count > _maxRecords)
                {
                    records = records.Skip(records.Count - _maxRecords).ToList();
                }

                var payload = string.Join(
                    Environment.NewLine,
                    records.Select(record => JsonSerializer.Serialize(record, JsonOptions)))
                    + Environment.NewLine;
                AtomicFileWriter.WriteAllBytes(Path, Encoding.UTF8.GetBytes(payload));
                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<OperationalActionJournalEntry> Read(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new OperationalActionJournal(layout).ReadCore();
    }

    private IReadOnlyList<OperationalActionJournalEntry> ReadCore()
    {
        if (!File.Exists(Path))
        {
            return Array.Empty<OperationalActionJournalEntry>();
        }

        var entries = new List<OperationalActionJournalEntry>();
        foreach (var line in File.ReadLines(Path))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<OperationalActionJournalEntry>(line, JsonOptions);
                if (entry is not null && IsSafe(entry))
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A malformed old record is ignored; new transitions remain
                // fail-closed when the journal cannot be rewritten.
            }
        }

        return entries;
    }

    private static bool IsSafe(OperationalActionJournalEntry entry)
    {
        return IsSafeCorrelationId(entry.CorrelationId)
            && IsSafeLifecycleToken(entry.ActionKind, allowNone: false)
            && IsSafeLifecycleToken(entry.Transition, allowNone: false)
            && IsSafeLifecycleToken(entry.Stage, allowNone: false)
            && IsSafeLifecycleToken(entry.OutcomeCode, allowNone: true)
            && entry.BuildVersion == BuildVersion.Current
            && entry.ElapsedMilliseconds >= 0
            && entry.AttemptNumber > 0;
    }

    internal static bool IsSafeToken(string? value, bool allowNone)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (allowNone || value != "none")
            && value.All(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_');
    }

    internal static bool IsSafeLifecycleToken(string? value, bool allowNone) =>
        IsSafeToken(value, allowNone);

    internal static bool IsSafeCorrelationId(string? value) =>
        Guid.TryParseExact(value, "N", out _);

    internal static bool IsCurrentBuildVersion(string? value) =>
        string.Equals(value, BuildVersion.Current, StringComparison.Ordinal);
}

/// <summary>
/// Single owner for operational action transitions. It has no timer and uses a
/// supplied clock so lifecycle tests remain deterministic.
/// </summary>
internal sealed class ResidentOperationalActionLifecycle
{
    private readonly OperationalActionJournal _journal;
    private readonly DefaultStorageLayout _layout;
    private readonly Func<long> _timestampMilliseconds;
    private readonly object _gate = new();
    private long _nextAttemptId;
    private int _attemptNumber;
    private long _startedAt;
    private OperationalActionState _state = OperationalActionState.Idle;
    private string _localReadinessStatus = "not_run";

    public ResidentOperationalActionLifecycle(
        DefaultStorageLayout layout,
        Func<long>? timestampMilliseconds = null,
        OperationalActionJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _timestampMilliseconds = timestampMilliseconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _journal = journal ?? new OperationalActionJournal(layout);
    }

    public event EventHandler? StateChanged;

    public OperationalActionState State
    {
        get
        {
            lock (_gate)
            {
                return _state.Status == "running"
                    ? _state with { ElapsedMilliseconds = Elapsed() }
                    : _state;
            }
        }
    }

    public string LocalReadinessStatus
    {
        get
        {
            lock (_gate)
            {
                return _localReadinessStatus;
            }
        }
    }

    public OperationalActionStartResult Start(
        string actionKind,
        string stage,
        bool userInputRequired,
        string nextAction)
    {
        if (!OperationalActionJournal.IsSafeLifecycleToken(actionKind, allowNone: false)
            || !OperationalActionJournal.IsSafeLifecycleToken(stage, allowNone: false)
            || !OperationalActionJournal.IsSafeLifecycleToken(nextAction, allowNone: false))
        {
            return new OperationalActionStartResult(false, "invalid_action_state", 0, "none");
        }

        lock (_gate)
        {
            if (_state.Status == "running")
            {
                return new OperationalActionStartResult(false, "action_in_progress", _state.AttemptId, _state.CorrelationId);
            }

            var attemptId = ++_nextAttemptId;
            var correlationId = Guid.NewGuid().ToString("N");
            _attemptNumber++;
            _startedAt = _timestampMilliseconds();
            if (actionKind == "local_readiness"
                && !ResidentOperationalReadinessProofStore.TryClear(_layout))
            {
                return new OperationalActionStartResult(false, "resident_readiness_proof_unavailable", 0, "none");
            }

            var candidate = new OperationalActionState(
                ActionKind: actionKind,
                Status: "running",
                Stage: stage,
                InputMode: userInputRequired ? "user_input" : "automatic",
                OutcomeCode: "none",
                NextAction: nextAction,
                CanCancel: true,
                AttemptId: attemptId,
                CorrelationId: correlationId,
                ElapsedMilliseconds: 0);
            if (!_journal.TryAppend(new OperationalActionJournalEntry(
                    correlationId,
                    actionKind,
                    "started",
                    stage,
                    "none",
                    0,
                    _attemptNumber,
                    BuildVersion.Current)))
            {
                return new OperationalActionStartResult(false, "journal_unavailable", 0, "none");
            }

            _state = candidate;
            if (actionKind == "local_readiness")
            {
                _localReadinessStatus = "checking";
            }
            RaiseChanged();
            return new OperationalActionStartResult(true, "started", attemptId, correlationId);
        }
    }

    public bool PublishStage(string stage, bool userInputRequired, string nextAction, long expectedAttemptId = 0)
    {
        if (!OperationalActionJournal.IsSafeLifecycleToken(stage, allowNone: false)
            || !OperationalActionJournal.IsSafeLifecycleToken(nextAction, allowNone: false))
        {
            return false;
        }

        lock (_gate)
        {
            if (!IsCurrentAttempt(expectedAttemptId))
            {
                return false;
            }

            var elapsed = Elapsed();
            if (!_journal.TryAppend(new OperationalActionJournalEntry(
                    _state.CorrelationId,
                    _state.ActionKind,
                    "stage",
                    stage,
                    "none",
                    elapsed,
                    _attemptNumber,
                    BuildVersion.Current)))
            {
                return false;
            }

            _state = _state with
            {
                Stage = stage,
                InputMode = userInputRequired ? "user_input" : "automatic",
                NextAction = nextAction,
                ElapsedMilliseconds = elapsed
            };
            RaiseChanged();
            return true;
        }
    }

    public bool Complete(string outcomeCode, string nextAction, long expectedAttemptId = 0)
    {
        var status = outcomeCode == "succeeded" ? "succeeded" : "failed";
        return Finish("completed", outcomeCode, nextAction, status, expectedAttemptId);
    }

    public bool Cancel(string outcomeCode = "cancelled", long expectedAttemptId = 0)
    {
        return Finish("cancelled", outcomeCode, "retry_action", "cancelled", expectedAttemptId);
    }

    private bool Finish(
        string transition,
        string outcomeCode,
        string nextAction,
        string status,
        long expectedAttemptId)
    {
        if (!OperationalActionJournal.IsSafeLifecycleToken(outcomeCode, allowNone: false)
            || !OperationalActionJournal.IsSafeLifecycleToken(nextAction, allowNone: true))
        {
            return false;
        }

        lock (_gate)
        {
            if (!IsCurrentAttempt(expectedAttemptId))
            {
                return false;
            }

            var elapsed = Elapsed();
            if (!_journal.TryAppend(new OperationalActionJournalEntry(
                    _state.CorrelationId,
                    _state.ActionKind,
                    transition,
                    _state.Stage,
                    outcomeCode,
                    elapsed,
                    _attemptNumber,
                    BuildVersion.Current)))
            {
                return false;
            }

            _state = _state with
            {
                Status = status,
                OutcomeCode = outcomeCode,
                NextAction = nextAction,
                CanCancel = false,
                ElapsedMilliseconds = elapsed
            };
            if (_state.ActionKind == "local_readiness")
            {
                _localReadinessStatus = status == "succeeded" ? "passed" : status;
            }
            RaiseChanged();
            return true;
        }
    }

    private long Elapsed()
    {
        return Math.Max(0, _timestampMilliseconds() - _startedAt);
    }

    private bool IsCurrentAttempt(long expectedAttemptId)
    {
        return _state.Status == "running"
            && expectedAttemptId > 0
            && expectedAttemptId == _state.AttemptId;
    }

    private void RaiseChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
