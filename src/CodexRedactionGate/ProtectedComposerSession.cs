using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal interface IProtectedComposerSessionFactory
{
    IProtectedComposerSession Create();
}

internal interface IProtectedComposerSession
{
    ProtectedComposerReadResult Read();

    ProtectedComposerTargetResult Revalidate();

    ProtectedComposerWriteResult WriteAndVerify(string text);

    ProtectedComposerReplayResult Replay();
}

internal sealed record ProtectedComposerTargetResult(
    bool Succeeded,
    string Status,
    TextSurfaceDescriptor? Surface,
    IReadOnlyDictionary<string, string> Diagnostics);

internal sealed record ProtectedComposerReadResult(
    bool Succeeded,
    string Status,
    TextSurfaceDescriptor? Surface,
    string? Text,
    IReadOnlyDictionary<string, string> Diagnostics);

internal sealed record ProtectedComposerWriteResult(
    bool Succeeded,
    string Status,
    TextSurfaceDescriptor? Surface,
    TextReplacementResult? Write,
    TextCaptureResult? Verification,
    IReadOnlyDictionary<string, string> Diagnostics);

internal sealed record ProtectedComposerReplayResult(
    bool Succeeded,
    string Status,
    TextSurfaceDescriptor? Surface,
    SubmitActionResult? Submit,
    IReadOnlyDictionary<string, string> Diagnostics);

/// <summary>
/// Owns one admitted composer's transient target and all access operations
/// performed against that target. It never owns admission or terminal state.
/// </summary>
internal sealed class ProtectedComposerSession : IProtectedComposerSession
{
    private enum SessionState
    {
        Created,
        Read,
        Verified,
        Replayed,
        Failed
    }

    private readonly IActiveTextSurfaceDiscovery _surfaceDiscovery;
    private readonly ITextSurfaceReader _reader;
    private readonly ITextSurfaceWriter _writer;
    private readonly ISubmitAction _submitAction;
    private readonly object _gate = new();
    private SessionState _state = SessionState.Created;
    private TextSurfaceDescriptor? _target;

    public ProtectedComposerSession(
        IActiveTextSurfaceDiscovery surfaceDiscovery,
        ITextSurfaceReader reader,
        ITextSurfaceWriter writer,
        ISubmitAction submitAction)
    {
        _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _submitAction = submitAction ?? throw new ArgumentNullException(nameof(submitAction));
    }

    public ProtectedComposerReadResult Read()
    {
        lock (_gate)
        {
            if (_state != SessionState.Created)
            {
                return FailRead("invalid_transition");
            }

            var discovered = DiscoverTarget();
            if (!discovered.Succeeded || discovered.Surface is null)
            {
                return FailRead(discovered.Status, discovered.Diagnostics);
            }

            var capture = CaptureText(discovered.Surface);
            if (!capture.Succeeded || capture.Text is null)
            {
                _state = SessionState.Failed;
                return new ProtectedComposerReadResult(
                    false,
                    OsInteractionStatusIds.CaptureFailed,
                    discovered.Surface,
                    null,
                    Merge(
                        discovered.Diagnostics,
                        capture.Diagnostics,
                        ("session_stage", "read"),
                        ("capture_status", capture.Status),
                        ("failed_closed", "true")));
            }

            _target = discovered.Surface;
            _state = SessionState.Read;
            return new ProtectedComposerReadResult(
                true,
                capture.Status,
                discovered.Surface,
                capture.Text,
                Merge(
                    discovered.Diagnostics,
                    capture.Diagnostics,
                    ("session_stage", "read")));
        }
    }

    public ProtectedComposerWriteResult WriteAndVerify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            if (_state != SessionState.Read)
            {
                return FailWrite("invalid_transition");
            }

            var beforeWrite = RevalidateTarget();
            if (!beforeWrite.Succeeded || beforeWrite.Surface is null)
            {
                return FailWrite(beforeWrite.Status, beforeWrite.Diagnostics);
            }

            var write = ReplaceText(beforeWrite.Surface, text);
            if (!write.Succeeded)
            {
                _state = SessionState.Failed;
                return new ProtectedComposerWriteResult(
                    false,
                    write.Status,
                    beforeWrite.Surface,
                    write,
                    null,
                    Merge(
                        beforeWrite.Diagnostics,
                        write.Diagnostics,
                        ("session_stage", "write"),
                        ("failed_closed", "true")));
            }

            var beforeVerify = RevalidateTarget();
            if (!beforeVerify.Succeeded || beforeVerify.Surface is null)
            {
                return new ProtectedComposerWriteResult(
                    false,
                    beforeVerify.Status,
                    beforeVerify.Surface ?? beforeWrite.Surface,
                    write,
                    null,
                    Merge(
                        beforeWrite.Diagnostics,
                        write.Diagnostics,
                        beforeVerify.Diagnostics,
                        ("session_stage", "verify_target"),
                        ("write_succeeded", "true"),
                        ("failed_closed", "true")));
            }

            var verification = CaptureText(beforeVerify.Surface);
            if (!verification.Succeeded
                || !string.Equals(verification.Text, text, StringComparison.Ordinal))
            {
                _state = SessionState.Failed;
                return new ProtectedComposerWriteResult(
                    false,
                    OsInteractionStatusIds.VerificationFailed,
                    beforeVerify.Surface,
                    write,
                    verification,
                    Merge(
                        beforeWrite.Diagnostics,
                        write.Diagnostics,
                        beforeVerify.Diagnostics,
                        verification.Diagnostics,
                        ("session_stage", "verify"),
                        ("write_succeeded", "true"),
                        ("verification_exact", "false"),
                        ("expected_length", text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("actual_length", verification.Text?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"),
                        ("failed_closed", "true")));
            }

            _state = SessionState.Verified;
            return new ProtectedComposerWriteResult(
                true,
                write.Status,
                beforeVerify.Surface,
                write,
                verification,
                Merge(
                    beforeWrite.Diagnostics,
                    write.Diagnostics,
                    beforeVerify.Diagnostics,
                    verification.Diagnostics,
                    ("session_stage", "verified"),
                    ("write_succeeded", "true"),
                    ("verification_exact", "true")));
        }
    }

    public ProtectedComposerTargetResult Revalidate()
    {
        lock (_gate)
        {
            if (_state is not (SessionState.Read or SessionState.Verified))
            {
                _state = SessionState.Failed;
                return new ProtectedComposerTargetResult(
                    false,
                    "invalid_transition",
                    _target,
                    new Dictionary<string, string>
                    {
                        ["session_stage"] = "failed",
                        ["failed_closed"] = "true"
                    });
            }

            var discovery = RevalidateTarget();
            return new ProtectedComposerTargetResult(
                discovery.Succeeded && discovery.Surface is not null,
                discovery.Status,
                discovery.Surface,
                discovery.Diagnostics);
        }
    }

    public ProtectedComposerReplayResult Replay()
    {
        lock (_gate)
        {
            if (_state is not (SessionState.Read or SessionState.Verified))
            {
                return FailReplay("invalid_transition");
            }

            var beforeReplay = RevalidateTarget();
            if (!beforeReplay.Succeeded || beforeReplay.Surface is null)
            {
                return FailReplay(beforeReplay.Status, beforeReplay.Diagnostics, beforeReplay.Surface);
            }

            var submit = Submit(beforeReplay.Surface);
            if (!submit.Succeeded)
            {
                _state = SessionState.Failed;
                return new ProtectedComposerReplayResult(
                    false,
                    submit.Status,
                    beforeReplay.Surface,
                    submit,
                    Merge(
                        beforeReplay.Diagnostics,
                        submit.Diagnostics,
                        ("session_stage", "replay"),
                        ("failed_closed", "true")));
            }

            _state = SessionState.Replayed;
            return new ProtectedComposerReplayResult(
                true,
                submit.Status,
                beforeReplay.Surface,
                submit,
                Merge(
                    beforeReplay.Diagnostics,
                    submit.Diagnostics,
                    ("session_stage", "replayed")));
        }
    }

    private TextSurfaceDiscoveryResult DiscoverTarget()
    {
        var discovery = DiscoverSurface();
        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            _state = SessionState.Failed;
            return TextSurfaceDiscoveryResult.Failure(
                discovery.Status,
                Merge(discovery.Diagnostics, ("session_stage", "target_discovery")));
        }

        return discovery;
    }

    private TextSurfaceDiscoveryResult RevalidateTarget()
    {
        var discovery = DiscoverSurface();
        if (!discovery.Succeeded || discovery.Surface is null)
        {
            _state = SessionState.Failed;
            var status = discovery.Status == OsInteractionStatusIds.StaleComposer
                ? OsInteractionStatusIds.StaleComposer
                : discovery.Status == OsInteractionStatusIds.FailedClosed
                    ? OsInteractionStatusIds.FailedClosed
                    : OsInteractionStatusIds.FocusLost;
            return TextSurfaceDiscoveryResult.Failure(
                status,
                Merge(
                    discovery.Diagnostics,
                    ("session_stage", "target_revalidation"),
                    ("rediscovery_status", discovery.Status),
                    ("failed_closed", "true")));
        }

        if (!discovery.Surface.Supported)
        {
            _state = SessionState.Failed;
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.FocusLost,
                Merge(
                    discovery.Diagnostics,
                    ("session_stage", "target_revalidation"),
                    ("rediscovery_status", discovery.Status),
                    ("failed_closed", "true")));
        }

        if (_target is null || !IsSameTarget(_target, discovery.Surface))
        {
            _state = SessionState.Failed;
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.StaleComposer,
                Merge(
                    discovery.Diagnostics,
                    ("session_stage", "target_revalidation"),
                    ("target_match", "false"),
                    ("failed_closed", "true")));
        }

        return discovery;
    }

    private TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        try
        {
            return _reader.CaptureText(surface);
        }
        catch (Exception exception)
        {
            return new TextCaptureResult(
                false,
                OsInteractionStatusIds.CaptureFailed,
                null,
                ExceptionDiagnostics("text_capture", exception));
        }
    }

    private TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        try
        {
            return _writer.ReplaceText(surface, text);
        }
        catch (Exception exception)
        {
            return new TextReplacementResult(
                false,
                OsInteractionStatusIds.WriteFailed,
                ExceptionDiagnostics("text_replace", exception));
        }
    }

    private SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        try
        {
            return _submitAction.Submit(surface);
        }
        catch (Exception exception)
        {
            return new SubmitActionResult(
                false,
                OsInteractionStatusIds.SubmitFailed,
                ExceptionDiagnostics("submit_replay", exception));
        }
    }

    private static IReadOnlyDictionary<string, string> ExceptionDiagnostics(
        string operation,
        Exception exception)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_operation"] = operation,
            ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["failed_closed"] = "true"
        };
    }

    private TextSurfaceDiscoveryResult DiscoverSurface()
    {
        try
        {
            return _surfaceDiscovery.DiscoverActiveSurface();
        }
        catch (Exception exception)
        {
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.FailedClosed,
                ExceptionDiagnostics("surface_discovery", exception));
        }
    }

    private static bool IsSameTarget(TextSurfaceDescriptor expected, TextSurfaceDescriptor actual)
    {
        if (!string.Equals(expected.SurfaceId, actual.SurfaceId, StringComparison.Ordinal)
            || !string.Equals(expected.ProfileId, actual.ProfileId, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedWindow = expected.Metadata.TryGetValue("window_handle");
        var actualWindow = actual.Metadata.TryGetValue("window_handle");
        return string.IsNullOrWhiteSpace(expectedWindow)
            || string.Equals(expectedWindow, actualWindow, StringComparison.Ordinal);
    }

    private ProtectedComposerReadResult FailRead(
        string status,
        IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        _state = SessionState.Failed;
        return new ProtectedComposerReadResult(
            false,
            status,
            null,
            null,
            Merge(diagnostics, ("session_stage", "failed"), ("failed_closed", "true")));
    }

    private ProtectedComposerWriteResult FailWrite(
        string status,
        IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        _state = SessionState.Failed;
        return new ProtectedComposerWriteResult(
            false,
            status,
            _target,
            null,
            null,
            Merge(diagnostics, ("session_stage", "failed"), ("failed_closed", "true")));
    }

    private ProtectedComposerReplayResult FailReplay(
        string status,
        IReadOnlyDictionary<string, string>? diagnostics = null,
        TextSurfaceDescriptor? surface = null)
    {
        _state = SessionState.Failed;
        return new ProtectedComposerReplayResult(
            false,
            status,
            surface ?? _target,
            null,
            Merge(diagnostics, ("session_stage", "failed"), ("failed_closed", "true")));
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? diagnostics,
        params (string Key, string Value)[] values)
    {
        var merged = diagnostics is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(diagnostics, StringComparer.Ordinal);
        foreach (var value in values)
        {
            merged[value.Key] = value.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second,
        params (string Key, string Value)[] values)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var item in second)
        {
            merged[item.Key] = item.Value;
        }

        return Merge(merged, values);
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second,
        IReadOnlyDictionary<string, string> third,
        params (string Key, string Value)[] values)
    {
        var merged = Merge(first, second);
        foreach (var item in third)
        {
            ((Dictionary<string, string>)merged)[item.Key] = item.Value;
        }

        return Merge(merged, values);
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second,
        IReadOnlyDictionary<string, string> third,
        IReadOnlyDictionary<string, string> fourth,
        params (string Key, string Value)[] values)
    {
        var merged = Merge(first, second, third);
        foreach (var item in fourth)
        {
            ((Dictionary<string, string>)merged)[item.Key] = item.Value;
        }

        return Merge(merged, values);
    }
}

internal sealed class ProtectedComposerSessionFactory : IProtectedComposerSessionFactory
{
    private readonly IActiveTextSurfaceDiscovery _surfaceDiscovery;
    private readonly ITextSurfaceReader _reader;
    private readonly ITextSurfaceWriter _writer;
    private readonly ISubmitAction _submitAction;

    public ProtectedComposerSessionFactory(
        IActiveTextSurfaceDiscovery surfaceDiscovery,
        ITextSurfaceReader reader,
        ITextSurfaceWriter writer,
        ISubmitAction submitAction)
    {
        _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _submitAction = submitAction ?? throw new ArgumentNullException(nameof(submitAction));
    }

    public IProtectedComposerSession Create()
    {
        return new ProtectedComposerSession(_surfaceDiscovery, _reader, _writer, _submitAction);
    }
}

/// <summary>
/// Production Windows composition. UIA/STA behavior remains inside the
/// verified Windows adapter; the session owns only its target-scoped lifetime.
/// </summary>
internal sealed class WindowsProtectedComposerSessionFactory : IProtectedComposerSessionFactory
{
    private readonly IActiveTextSurfaceDiscovery _surfaceDiscovery;
    private readonly WindowsVerifiedComposerSurfaceAdapter _adapter;
    private readonly ISubmitAction _submitAction;

    public WindowsProtectedComposerSessionFactory(
        IActiveTextSurfaceDiscovery surfaceDiscovery,
        WindowsVerifiedComposerSurfaceAdapter adapter,
        ISubmitAction? submitAction = null)
    {
        _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _submitAction = submitAction ?? adapter;
    }

    public IProtectedComposerSession Create()
    {
        return new ProtectedComposerSession(_surfaceDiscovery, _adapter, _adapter, _submitAction);
    }
}

/// <summary>
/// Deterministic reference composition with the same session contract as
/// production. It has no access to persisted AI profiles or cloud transport.
/// </summary>
internal sealed class ReferenceProtectedComposerSessionFactory : IProtectedComposerSessionFactory
{
    private readonly ProtectedComposerSessionFactory _inner;

    public ReferenceProtectedComposerSessionFactory(
        IActiveTextSurfaceDiscovery surfaceDiscovery,
        ITextSurfaceReader reader,
        ITextSurfaceWriter writer,
        ISubmitAction submitAction)
    {
        _inner = new ProtectedComposerSessionFactory(surfaceDiscovery, reader, writer, submitAction);
    }

    public IProtectedComposerSession Create() => _inner.Create();
}
