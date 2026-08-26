using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexRedactionGate;

internal delegate OsInteractionResult ResidentCanaryRunDelegate(
    NativeSubmitRuntime runtime,
    NativeSubmitTargetIdentity target,
    ResidentCanaryArm arm,
    Func<string, string, bool> traceStage,
    Func<bool> executionGuard,
    Func<IDisposable?> executionLease);

internal sealed record ResidentCanaryArm(
    long AttemptId,
    string ProfileId,
    string Marker,
    long TargetGeneration);

internal sealed record ResidentCanaryStartResult(
    bool Started,
    string Code,
    ResidentCanaryArm? Arm);

internal sealed record ResidentCanaryExecutionResult(
    bool Succeeded,
    string Code,
    long AttemptId,
    string ProfileId,
    long TargetGeneration,
    IReadOnlyList<string> Stages,
    bool CleanupSucceeded,
    bool SensitiveContentExcluded)
{
    internal static ResidentCanaryExecutionResult Failed(
        ResidentCanaryArm arm,
        string code,
        IReadOnlyList<string> stages,
        bool cleanupSucceeded = true) => new(
            false,
            code,
            arm.AttemptId,
            arm.ProfileId,
            arm.TargetGeneration,
            stages,
            cleanupSucceeded,
            true);

    internal static ResidentCanaryExecutionResult Passed(
        ResidentCanaryArm arm,
        IReadOnlyList<string> stages) => new(
            true,
            "passed",
            arm.AttemptId,
            arm.ProfileId,
            arm.TargetGeneration,
            stages,
            true,
            true);
}

internal sealed class ResidentCanaryLifecycle
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["none"] = Set("requested"),
            ["requested"] = Set("armed", "cancelled"),
            ["armed"] = Set("send_observed", "terminal_failed", "cancelled"),
            ["send_observed"] = Set("transaction_started", "terminal_failed", "cancelled"),
            ["transaction_started"] = Set("overlay_created", "terminal_failed", "cancelled"),
            ["overlay_created"] = Set("sanitized_written", "terminal_failed", "cancelled"),
            ["sanitized_written"] = Set("replay_verified", "terminal_failed", "cancelled"),
            ["replay_verified"] = Set("terminal_passed", "terminal_failed"),
            ["terminal_passed"] = Set(),
            ["terminal_failed"] = Set(),
            ["cancelled"] = Set()
        };

    private readonly List<string> _stages = new();

    internal string Current => _stages.Count == 0 ? "none" : _stages[^1];

    internal IReadOnlyList<string> Stages => _stages.AsReadOnly();

    internal bool IsTerminal => Current is "terminal_passed" or "terminal_failed" or "cancelled";

    internal bool TryAdvance(string expectedCurrent, string next)
    {
        if (!string.Equals(Current, expectedCurrent, StringComparison.Ordinal)
            || !AllowedTransitions.TryGetValue(expectedCurrent, out var allowed)
            || !allowed.Contains(next))
        {
            return false;
        }

        _stages.Add(next);
        return true;
    }

    internal bool TryAdvance(string next) => TryAdvance(Current, next);

    private static IReadOnlySet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.Ordinal);
}

internal sealed class ResidentCanarySession
{
    private readonly object _gate = new();
    private ResidentCanaryArm? _arm;
    private ResidentCanaryLifecycle? _lifecycle;

    internal ResidentCanaryStartResult Arm(
        long attemptId,
        string profileId,
        long targetGeneration = 1)
    {
        if (attemptId <= 0
            || targetGeneration < 0
            || string.IsNullOrWhiteSpace(profileId))
        {
            return new ResidentCanaryStartResult(false, "invalid_canary_identity", null);
        }

        lock (_gate)
        {
            if (_arm is not null && _lifecycle is not null && !_lifecycle.IsTerminal)
            {
                return new ResidentCanaryStartResult(false, "canary_in_progress", null);
            }

            var marker = "CS_CANARY_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var lifecycle = new ResidentCanaryLifecycle();
            if (!lifecycle.TryAdvance("none", "requested")
                || !lifecycle.TryAdvance("requested", "armed"))
            {
                return new ResidentCanaryStartResult(false, "canary_state_unavailable", null);
            }

            _arm = new ResidentCanaryArm(attemptId, profileId, marker, targetGeneration);
            _lifecycle = lifecycle;
            return new ResidentCanaryStartResult(true, "armed", _arm);
        }
    }

    internal bool TryObserveSend(
        long attemptId,
        string profileId,
        long targetGeneration,
        out ResidentCanaryArm arm)
    {
        lock (_gate)
        {
            if (_arm is null
                || _lifecycle is null
                || _arm.AttemptId != attemptId
                || !string.Equals(_arm.ProfileId, profileId, StringComparison.Ordinal)
                || _arm.TargetGeneration != targetGeneration
                || !_lifecycle.TryAdvance("armed", "send_observed"))
            {
                arm = null!;
                return false;
            }

            arm = _arm;
            return true;
        }
    }

    internal bool TryRecordStage(long attemptId, string stage)
    {
        lock (_gate)
        {
            if (_arm is null
                || _lifecycle is null
                || _arm.AttemptId != attemptId)
            {
                return false;
            }

            return stage switch
            {
                "transaction_started" => _lifecycle.TryAdvance("transaction_started"),
                "overlay_created" => _lifecycle.TryAdvance("overlay_created"),
                "sanitized_written" => _lifecycle.TryAdvance("sanitized_written"),
                "replay_verified" => _lifecycle.TryAdvance("replay_verified"),
                _ => false
            };
        }
    }

    internal IReadOnlyList<string> Stages(long attemptId)
    {
        lock (_gate)
        {
            return _arm is not null
                && _lifecycle is not null
                && _arm.AttemptId == attemptId
                ? _lifecycle.Stages.ToArray()
                : Array.Empty<string>();
        }
    }

    internal ResidentCanaryExecutionResult Complete(
        ResidentCanaryArm arm,
        bool succeeded,
        string code,
        bool cleanupSucceeded)
    {
        lock (_gate)
        {
            if (_arm is null || _lifecycle is null || !ReferenceEquals(_arm, arm))
            {
                return ResidentCanaryExecutionResult.Failed(
                    arm,
                    "stale_canary_attempt",
                    new[] { "terminal_failed" },
                    cleanupSucceeded: false);
            }

            var terminal = succeeded && cleanupSucceeded ? "terminal_passed" : "terminal_failed";
            if (!_lifecycle.TryAdvance(terminal))
            {
                return ResidentCanaryExecutionResult.Failed(
                    arm,
                    "canary_terminal_transition_failed",
                    _lifecycle.Stages,
                    cleanupSucceeded: false);
            }

            var result = succeeded && cleanupSucceeded
                ? ResidentCanaryExecutionResult.Passed(arm, _lifecycle.Stages)
                : ResidentCanaryExecutionResult.Failed(
                    arm,
                    succeeded && !cleanupSucceeded ? "cleanup_failed" : code,
                    _lifecycle.Stages,
                    cleanupSucceeded);
            return result with { CleanupSucceeded = cleanupSucceeded };
        }
    }

    internal bool TryCancel(long attemptId, out ResidentCanaryArm? arm)
    {
        lock (_gate)
        {
            arm = _arm;
            return _arm is not null
                && _lifecycle is not null
                && _arm.AttemptId == attemptId
                && !_lifecycle.IsTerminal
                && _lifecycle.TryAdvance("cancelled");
        }
    }

    internal bool TryCancel(long attemptId) => TryCancel(attemptId, out _);

    internal bool TryFail(
        long attemptId,
        string code,
        out ResidentCanaryExecutionResult result)
    {
        lock (_gate)
        {
            if (_arm is null
                || _lifecycle is null
                || _arm.AttemptId != attemptId
                || _lifecycle.IsTerminal
                || !_lifecycle.TryAdvance("terminal_failed"))
            {
                result = null!;
                return false;
            }

            result = ResidentCanaryExecutionResult.Failed(
                _arm,
                code,
                _lifecycle.Stages,
                cleanupSucceeded: true);
            return true;
        }
    }
}

internal static class ResidentCanaryBuildIdentity
{
    internal static string ExecutableSha256()
    {
        try
        {
            var path = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : "unbound";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unbound";
        }
    }

    internal static string SourceCommit()
    {
        var version = BuildVersion.Current;
        var marker = version.LastIndexOf('+');
        var candidate = marker >= 0 ? version[(marker + 1)..] : "unbound";
        return candidate.Length is >= 7 and <= 64
            && candidate.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9')
            ? candidate
            : "unbound";
    }

    internal static string InstallerIdentity()
    {
        var processPath = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
        return directory is null ? "unbound" : ReadInstallerIdentity(directory);
    }

    internal static string ReadInstallerIdentity(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "unbound";
        }

        try
        {
            var path = Path.Combine(directory, "install-identity.txt");
            if (!File.Exists(path))
            {
                return "unbound";
            }

            foreach (var line in File.ReadLines(path))
            {
                const string prefix = "installer_identity=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var identity = line[prefix.Length..].Trim();
                return identity.Length > 0 && identity.Length <= 256
                    && identity.All(character => char.IsLetterOrDigit(character)
                        || character is '.' or '-' or '_')
                    ? identity
                    : "unbound";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unbound";
        }

        return "unbound";
    }

    internal static string SafeCompatibilityFingerprint(TextSurfaceDescriptor surface)
    {
        var value = surface.Metadata.TryGetValue("compatibility_fingerprint")
            ?? surface.ProfileId;
        return OpaqueFingerprint.FromSource(value).Value;
    }

    internal static string SafeBinding(SubmitBindingProfile profile)
    {
        var value = profile.SubmitBinding?.DisplayText ?? "unknown";
        return new string(value
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Trim('_')
            .ToLowerInvariant() is { Length: > 0 } safe
            ? safe
            : "unknown";
    }
}

internal sealed class ResidentCanaryProductionRunner : IDisposable
{
    private readonly DefaultStorageLayout _layout;
    private readonly IActiveTextSurfaceDiscovery _baseDiscovery;
    private readonly IConfirmationOverlay _confirmationOverlay;

    internal ResidentCanaryProductionRunner(
        DefaultStorageLayout layout,
        IActiveTextSurfaceDiscovery baseDiscovery,
        IConfirmationOverlay confirmationOverlay)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _baseDiscovery = baseDiscovery ?? throw new ArgumentNullException(nameof(baseDiscovery));
        _confirmationOverlay = confirmationOverlay ?? throw new ArgumentNullException(nameof(confirmationOverlay));
    }

    internal OsInteractionResult Run(
        NativeSubmitTargetIdentity target,
        ResidentCanaryArm arm,
        Func<string, string, bool> traceStage,
        Func<bool> executionGuard,
        Func<IDisposable?> executionLease)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(traceStage);
        ArgumentNullException.ThrowIfNull(executionGuard);
        ArgumentNullException.ThrowIfNull(executionLease);

        var composerDiscovery = new CapturedTargetSurfaceDiscovery(_baseDiscovery, target);
        var liveAdapter = OperatingSystem.IsWindows()
            ? new WindowsVerifiedComposerSurfaceAdapter(
                new NativeVerifiedComposerTextAccess(composerDiscovery.DiscoverActiveSurface))
            : new WindowsVerifiedComposerSurfaceAdapter();
        var discovery = composerDiscovery.DiscoverActiveSurface();
        if (!discovery.Succeeded || discovery.Surface is null)
        {
            return Failure(discovery.Status, discovery.Surface, new Dictionary<string, string>
            {
                ["canary_stage"] = "target_verification",
                ["canary_cleanup"] = "true",
                ["cloud_submission"] = "false"
            });
        }

        var capture = liveAdapter.CaptureText(discovery.Surface);
        if (!capture.Succeeded || capture.Text is null)
        {
            return Failure(OsInteractionStatusIds.CaptureFailed, discovery.Surface, new Dictionary<string, string>
            {
                ["canary_stage"] = "marker_check",
                ["canary_code"] = "capture_failed",
                ["canary_cleanup"] = "true",
                ["cloud_submission"] = "false"
            });
        }

        if (!capture.Text.Contains(arm.Marker, StringComparison.Ordinal))
        {
            return Failure(OsInteractionStatusIds.FailedClosed, discovery.Surface, new Dictionary<string, string>
            {
                ["canary_stage"] = "marker_check",
                ["canary_code"] = "marker_missing",
                ["canary_cleanup"] = "true",
                ["cloud_submission"] = "false"
            });
        }

        var sanitizer = Sanitizer.CreateProduction(
            _layout,
            new[]
            {
                new DictionaryTerm(
                    "resident_canary",
                    arm.Marker,
                    PolicyActions.PseudonymizeRestorable,
                    "resident canary")
            });
        var orchestrator = new OsInteractionOrchestrator(
            sanitizer,
            composerDiscovery,
            liveAdapter,
            liveAdapter,
            new ResidentCanaryReplayUnavailableAction(),
            _confirmationOverlay);
        var interaction = orchestrator.RunOnce(
            OsInteractionRunOptions.ConfirmAndSend,
            traceStage,
            executionGuard,
            executionLease);

        var cleanupSucceeded = RestoreExactText(
            liveAdapter,
            liveAdapter,
            discovery.Surface,
            capture.Text);
        var diagnostics = new Dictionary<string, string>(interaction.Diagnostics, StringComparer.Ordinal)
        {
            ["canary_cleanup"] = cleanupSucceeded.ToString().ToLowerInvariant(),
            ["canary_marker_present"] = "true",
            ["cloud_submission"] = "false"
        };
        if (!cleanupSucceeded)
        {
            return interaction with
            {
                Status = OsInteractionStatusIds.FailedClosed,
                Applied = false,
                Submitted = false,
                Diagnostics = diagnostics
            };
        }

        return interaction with { Diagnostics = diagnostics };
    }

    private static bool RestoreExactText(
        ITextSurfaceWriter writer,
        ITextSurfaceReader reader,
        TextSurfaceDescriptor surface,
        string originalText)
    {
        var replacement = writer.ReplaceText(surface, originalText);
        if (!replacement.Succeeded)
        {
            return false;
        }

        var verification = reader.CaptureText(surface);
        return verification.Succeeded
            && string.Equals(verification.Text, originalText, StringComparison.Ordinal);
    }

    private static OsInteractionResult Failure(
        string status,
        TextSurfaceDescriptor? surface,
        IReadOnlyDictionary<string, string> diagnostics) => new(
            status,
            surface,
            null,
            null,
            false,
            false,
            diagnostics);

    public void Dispose()
    {
        (_confirmationOverlay as IDisposable)?.Dispose();
    }

}

/// <summary>
/// Keeps the installed canary harmless until the production replay seam can
/// prove that the replay key is intercepted and never reaches the cloud.
/// Returning success here would make the canary a false green signal.
/// </summary>
internal sealed class ResidentCanaryReplayUnavailableAction : ISubmitAction
{
    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return new SubmitActionResult(false, OsInteractionStatusIds.ReplayIndeterminate, new Dictionary<string, string>
        {
            ["cloud_submission"] = "false",
            ["canary_replay"] = "not_executed",
            ["canary_code"] = "replay_unavailable"
        });
    }
}

internal sealed class ResidentCanaryResourceOwner : IDisposable
{
    private readonly IDisposable? _first;
    private readonly IDisposable? _second;
    private int _disposed;

    internal ResidentCanaryResourceOwner(IDisposable? first, IDisposable? second)
    {
        _first = first;
        _second = second;
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _second?.Dispose();
        _first?.Dispose();
    }
}

internal sealed record ResidentCanaryEvidence(
    string SchemaVersion,
    string TicketId,
    string BehaviorId,
    long AttemptId,
    string Outcome,
    string TerminalReason,
    string ProfileId,
    long TargetGeneration,
    string SubmitBinding,
    IReadOnlyList<string> Stages,
    bool CleanupSucceeded,
    bool SensitiveContentExcluded,
    string BuildVersion,
    string SourceCommit,
    string ExecutableSha256,
    string InstallerIdentity,
    string CompatibilityFingerprint)
{
    internal static ResidentCanaryEvidence Passed(
        long attemptId,
        string profileId,
        long targetGeneration,
        string submitBinding,
        IReadOnlyList<string> stages,
        bool cleanupSucceeded,
        string buildVersion,
        string sourceCommit,
        string executableSha256,
        string installerIdentity,
        string compatibilityFingerprint) => Create(
            attemptId,
            profileId,
            targetGeneration,
            submitBinding,
            stages,
            cleanupSucceeded,
            buildVersion,
            sourceCommit,
            executableSha256,
            installerIdentity,
            compatibilityFingerprint,
            "passed",
            "terminal_passed");

    internal static ResidentCanaryEvidence Failed(
        long attemptId,
        string profileId,
        long targetGeneration,
        string submitBinding,
        IReadOnlyList<string> stages,
        string reason,
        bool cleanupSucceeded,
        string buildVersion,
        string sourceCommit,
        string executableSha256,
        string installerIdentity,
        string compatibilityFingerprint) => Create(
            attemptId,
            profileId,
            targetGeneration,
            submitBinding,
            stages,
            cleanupSucceeded,
            buildVersion,
            sourceCommit,
            executableSha256,
            installerIdentity,
            compatibilityFingerprint,
            "failed",
            reason);

    private static ResidentCanaryEvidence Create(
        long attemptId,
        string profileId,
        long targetGeneration,
        string submitBinding,
        IReadOnlyList<string> stages,
        bool cleanupSucceeded,
        string buildVersion,
        string sourceCommit,
        string executableSha256,
        string installerIdentity,
        string compatibilityFingerprint,
        string outcome,
        string terminalReason) => new(
            "1",
            "352",
            "installed_keyboard_canary",
            attemptId,
            outcome,
            terminalReason,
            profileId,
            targetGeneration,
            submitBinding,
            stages.ToArray(),
            cleanupSucceeded,
            true,
            buildVersion,
            sourceCommit,
            executableSha256,
            installerIdentity,
            compatibilityFingerprint);
}

internal sealed class ResidentCanaryEvidenceStore
{
    private const string FileName = "resident-canary.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly DefaultStorageLayout _layout;

    internal ResidentCanaryEvidenceStore(DefaultStorageLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    internal string Path => System.IO.Path.Combine(_layout.SettingsDirectory, FileName);

    internal bool TrySave(ResidentCanaryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!IsSafe(evidence))
        {
            return false;
        }

        try
        {
            _layout.EnsureDirectories();
            var payload = JsonSerializer.Serialize(evidence, JsonOptions);
            AtomicFileWriter.WriteAllBytes(Path, Encoding.UTF8.GetBytes(payload));
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException)
        {
            return false;
        }
    }

    internal bool TryLoad(out ResidentCanaryEvidence? evidence)
    {
        evidence = null;
        try
        {
            if (!File.Exists(Path))
            {
                return false;
            }

            var loaded = JsonSerializer.Deserialize<ResidentCanaryEvidence>(File.ReadAllText(Path), JsonOptions);
            if (loaded is null || !IsSafe(loaded))
            {
                return false;
            }

            evidence = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsSafe(ResidentCanaryEvidence evidence)
    {
        return evidence.SchemaVersion == "1"
            && evidence.TicketId == "352"
            && IsToken(evidence.BehaviorId)
            && evidence.AttemptId > 0
            && evidence.Outcome is "passed" or "failed"
            && IsToken(evidence.TerminalReason)
            && IsProfile(evidence.ProfileId)
            && evidence.TargetGeneration >= 0
            && IsBinding(evidence.SubmitBinding)
            && evidence.Stages is not null
            && evidence.Stages.Count > 0
            && evidence.Stages.All(IsToken)
            && (evidence.Outcome != "passed" || evidence.CleanupSucceeded)
            && evidence.SensitiveContentExcluded
            && IsVersion(evidence.BuildVersion)
            && IsCommit(evidence.SourceCommit)
            && IsHashOrUnbound(evidence.ExecutableSha256)
            && IsVersionOrUnbound(evidence.InstallerIdentity)
            && IsHashOrUnbound(evidence.CompatibilityFingerprint);
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_');

    private static bool IsProfile(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsBinding(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '+' or '_' or '-');

    private static bool IsVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    private static bool IsCommit(string? value) => value == "unbound" || IsHash(value);

    private static bool IsHashOrUnbound(string? value) => value == "unbound" || IsHash(value);

    private static bool IsVersionOrUnbound(string? value) => value == "unbound" || IsVersion(value);

    private static bool IsHash(string? value) =>
        value is not null
        && value.Length is >= 7 and <= 64
        && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
}
