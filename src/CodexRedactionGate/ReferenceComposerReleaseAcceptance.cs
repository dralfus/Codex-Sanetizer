using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexRedactionGate;

internal sealed record ReferenceComposerReleaseScenarioResult(
    string ScenarioId,
    bool Passed,
    bool RawFree,
    bool CleanupPassed,
    string Status,
    string TerminalStatus,
    string ComposerAccess,
    string EvidenceLevel);

internal sealed record ReferenceComposerReleaseAcceptanceReport(
    string Status,
    bool InteractiveDesktopAvailable,
    IReadOnlyList<ReferenceComposerReleaseScenarioResult> Scenarios,
    bool CleanupPassed,
    string BuildIdentifier)
{
    public bool Passed => Status == "passed"
        && InteractiveDesktopAvailable
        && CleanupPassed
        && ReferenceComposerReleaseAcceptanceRunner.HasPassedReleaseScenarios(Scenarios);
}

internal static class ReferenceComposerInteractiveDesktop
{
    private const uint DesktopReadObjects = 0x0001;

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return false;
        }

        var desktop = NativeMethods.OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseDesktop(desktop);
        return true;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);
    }
}

internal static class ReferenceComposerReleaseAcceptanceRunner
{
    private const string SensitivePrompt = "Connect to 192.168.10.25";
    private const string MultilineSensitivePrompt = "first line\nConnect to 192.168.10.25\nlast line";

    internal static ReferenceComposerReleaseAcceptanceReport Run(
        byte[] hmacSecret,
        Func<bool>? interactiveDesktopProbe = null)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        bool interactive;
        try
        {
            interactive = interactiveDesktopProbe?.Invoke()
                ?? ReferenceComposerInteractiveDesktop.IsAvailable();
        }
        catch
        {
            return new ReferenceComposerReleaseAcceptanceReport(
                "failed_closed",
                InteractiveDesktopAvailable: false,
                Array.Empty<ReferenceComposerReleaseScenarioResult>(),
                CleanupPassed: false,
                BuildIdentifier: CurrentBuildIdentifier());
        }
        if (!interactive)
        {
            return new ReferenceComposerReleaseAcceptanceReport(
                "interactive_desktop_unavailable",
                InteractiveDesktopAvailable: false,
                Array.Empty<ReferenceComposerReleaseScenarioResult>(),
                CleanupPassed: false,
                BuildIdentifier: CurrentBuildIdentifier());
        }

        var scenarios = new List<ReferenceComposerReleaseScenarioResult>();
        var firstRun = RunMatrix(hmacSecret, "run1");
        var secondRun = RunMatrix(hmacSecret, "run2");
        scenarios.AddRange(firstRun);
        scenarios.AddRange(secondRun);

        var releaseScenariosPassed = HasPassedReleaseScenarios(scenarios);
        var cleanupPassed = releaseScenariosPassed
            && scenarios.All(scenario => scenario.CleanupPassed);
        return new ReferenceComposerReleaseAcceptanceReport(
            releaseScenariosPassed && cleanupPassed
                ? "passed"
                : "failed_closed",
            InteractiveDesktopAvailable: true,
            scenarios,
            cleanupPassed,
            CurrentBuildIdentifier());
    }

    internal static IReadOnlyList<string> RenderRawFree(
        ReferenceComposerReleaseAcceptanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"status: {SafeStatus(report.Status)}",
            $"interactive_desktop: {report.InteractiveDesktopAvailable.ToString().ToLowerInvariant()}",
            $"build_id: {SafeStatus(report.BuildIdentifier)}"
        };
        foreach (var scenario in report.Scenarios)
        {
            lines.Add(
                $"scenario: {SafeStatus(scenario.ScenarioId)} status: {SafeStatus(scenario.Status)} terminal_status: {SafeStatus(scenario.TerminalStatus)} composer_access: {SafeStatus(scenario.ComposerAccess)} evidence_level: {SafeStatus(scenario.EvidenceLevel)} raw_free: {scenario.RawFree.ToString().ToLowerInvariant()} cleanup: {scenario.CleanupPassed.ToString().ToLowerInvariant()}");
        }

        lines.Add($"cleanup: {report.CleanupPassed.ToString().ToLowerInvariant()}");
        lines.Add($"overall: {(report.Passed ? "passed" : "failed_closed")}");
        return lines;
    }

    /// <summary>
    /// Public release-projection seam for the mandatory unavailable-native-access
    /// RED case. It intentionally cannot become publishable release evidence.
    /// </summary>
    internal static ReferenceComposerReleaseScenarioResult RunUnavailableProductionAccessProbe(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);
        return RunExpectedBlockedScenario("production_access_unavailable", () => ReferenceComposerAcceptanceRunner.Run(
            CreateSanitizer(hmacSecret),
            SensitivePrompt,
            ReferenceComposerDecision.Approve,
            clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary,
            composerAccessMode: ReferenceComposerAccessMode.Unavailable));
    }

    private static IReadOnlyList<ReferenceComposerReleaseScenarioResult> RunMatrix(
        byte[] hmacSecret,
        string runId)
    {
        var expectedMultilineSanitized = SanitizeForReference(hmacSecret, MultilineSensitivePrompt);
        return new[]
        {
            RunScenario($"{runId}.safe_prompt", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                "A harmless local prompt",
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => report.HookStarted
                    && report.OriginalInputSuppressed
                    && report.Submitted
                    && report.CleanupPassed
                    && ProtectedSendTrace.IsCompleteSafeSendTrace(report.Trace)
                    && report.SentTexts.Count == 1),
            RunExpectedBlockedScenario($"{runId}.production_access_unavailable", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary,
                composerAccessMode: ReferenceComposerAccessMode.Unavailable)),
            RunScenario($"{runId}.sensitive_prompt", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => report.HookStarted
                    && report.OriginalInputSuppressed
                    && report.Submitted
                    && report.CleanupPassed
                    && ProtectedSendTrace.IsCompleteSafeSendTrace(report.Trace)
                    && report.Trace.Any(entry => entry.Stage == "overlay_foreground_confirmed")
                    && report.SentTexts.Count == 1
                    && !report.SentTexts[0].Contains("192.168.10.25", StringComparison.Ordinal)),
            RunScenario($"{runId}.multiline_sensitive_prompt", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                MultilineSensitivePrompt,
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => report.HookStarted
                    && report.OriginalInputSuppressed
                    && report.Submitted
                    && report.CleanupPassed
                    && ProtectedSendTrace.IsCompleteSafeSendTrace(report.Trace)
                    && report.SentTexts.Count == 1
                    && string.Equals(
                        ComposerTextFormatting.NormalizeLineEndings(report.SentTexts[0]),
                        expectedMultilineSanitized,
                        StringComparison.Ordinal)
                    && !report.SentTexts[0].Contains("192.168.10.25", StringComparison.Ordinal)),
            RunScenario($"{runId}.cancel", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Cancel,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => report.HookStarted
                    && report.OriginalInputSuppressed
                    && !report.Submitted
                    && report.CleanupPassed
                    && report.SentTexts.Count == 0
                    && IsCompleteCancellationTrace(report.Trace)),
            RunScenario($"{runId}.foreground_refusal", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                ReferenceComposerForegroundMode.Refused,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                BlockedScenario),
            RunScenario($"{runId}.target_change_before_write", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                targetChangeMode: ReferenceComposerTargetChangeMode.BeforeWrite,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                BlockedScenario),
            RunScenario($"{runId}.target_change_before_replay", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                targetChangeMode: ReferenceComposerTargetChangeMode.BeforeReplay,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                BlockedScenario),
            RunScenario($"{runId}.uia_write_failure", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                writeMode: ReferenceComposerWriteMode.Unavailable,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => BlockedScenario(report)
                    && report.Trace.All(entry => entry.Stage != "text_written")),
            RunScenario($"{runId}.replay_unavailable", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                replayMode: ReferenceComposerReplayMode.Unavailable,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => ReplayFailureScenario(report, OsInteractionStatusIds.ReplayUnavailable)),
            RunScenario($"{runId}.replay_partial", () => ReferenceComposerAcceptanceRunner.Run(
                CreateSanitizer(hmacSecret),
                SensitivePrompt,
                ReferenceComposerDecision.Approve,
                replayMode: ReferenceComposerReplayMode.Partial,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary),
                report => ReplayFailureScenario(report, OsInteractionStatusIds.ReplayIndeterminate))
        };
    }

    private static ReferenceComposerReleaseScenarioResult RunScenario(
        string scenarioId,
        Func<ReferenceComposerAcceptanceReport> run,
        Func<ReferenceComposerAcceptanceReport, bool> passed)
    {
        try
        {
            var report = run();

            // The release projection only trusts the single reference
            // production-access evidence level. Missing, wrong or stronger
            // evidence (live / installed-application) fails the scenario closed
            // and is never rendered as installed ChatGPT proof.
            var evidenceLevel = ReferenceComposerEvidenceLevelTokens.SelectForPublication(report.EvidenceLevel);
            var evidenceToken = ReferenceComposerEvidenceLevelTokens.ToToken(evidenceLevel);
            var scenarioPassed = passed(report)
                && ReferenceComposerEvidenceLevelTokens.TryValidate(evidenceLevel);
            var rawFree = (ProtectedSendTrace.IsValidTerminalTrace(report.Trace)
                    || IsRawFreeBlockedTerminal(report.Trace))
                && report.SentTexts.All(text => !text.Contains(SensitivePrompt, StringComparison.Ordinal));
            var composerAccess = report.ReplayDiagnostics.TryGetValue("composer_access", out var reportedComposerAccess)
                ? reportedComposerAccess
                : "unavailable";
            return new ReferenceComposerReleaseScenarioResult(
                scenarioId,
                scenarioPassed,
                rawFree,
                CleanupPassed: report.CleanupPassed,
                Status: scenarioPassed ? "passed" : "failed_closed",
                TerminalStatus: report.Trace.LastOrDefault()?.ResultCode ?? OsInteractionStatusIds.FailedClosed,
                ComposerAccess: composerAccess,
                EvidenceLevel: evidenceToken);
        }
        catch
        {
            return new ReferenceComposerReleaseScenarioResult(
                scenarioId,
                Passed: false,
                RawFree: false,
                CleanupPassed: false,
                Status: "failed_closed",
                TerminalStatus: OsInteractionStatusIds.FailedClosed,
                ComposerAccess: "unavailable",
                EvidenceLevel: ReferenceComposerEvidenceLevelTokens.UnavailableToken);
        }
    }

    private static ReferenceComposerReleaseScenarioResult RunExpectedBlockedScenario(
        string scenarioId,
        Func<ReferenceComposerAcceptanceReport> run)
    {
        try
        {
            var report = run();
            var evidenceLevel = ReferenceComposerEvidenceLevelTokens.SelectForPublication(report.EvidenceLevel);
            var evidenceToken = ReferenceComposerEvidenceLevelTokens.ToToken(evidenceLevel);
            var rawFree = IsRawFreeBlockedTerminal(report.Trace)
                && report.SentTexts.All(text => !text.Contains(SensitivePrompt, StringComparison.Ordinal));
            var composerAccess = report.ReplayDiagnostics.TryGetValue("composer_access", out var reportedComposerAccess)
                ? reportedComposerAccess
                : "unavailable";
            var expectedBlocked = IsExpectedUnavailableProductionAccess(
                report,
                rawFree,
                composerAccess,
                evidenceLevel);
            return new ReferenceComposerReleaseScenarioResult(
                scenarioId,
                Passed: false,
                RawFree: rawFree,
                CleanupPassed: report.CleanupPassed,
                Status: expectedBlocked ? "expected_blocked" : "failed_closed",
                TerminalStatus: report.Trace.LastOrDefault()?.ResultCode ?? OsInteractionStatusIds.FailedClosed,
                ComposerAccess: composerAccess,
                EvidenceLevel: evidenceToken);
        }
        catch
        {
            return new ReferenceComposerReleaseScenarioResult(
                scenarioId,
                Passed: false,
                RawFree: false,
                CleanupPassed: false,
                Status: "failed_closed",
                TerminalStatus: OsInteractionStatusIds.FailedClosed,
                ComposerAccess: "unavailable",
                EvidenceLevel: ReferenceComposerEvidenceLevelTokens.UnavailableToken);
        }
    }

    internal static bool HasPassedReleaseScenarios(IReadOnlyList<ReferenceComposerReleaseScenarioResult> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var negativeControls = scenarios
            .Where(scenario => scenario.ScenarioId.EndsWith(".production_access_unavailable", StringComparison.Ordinal))
            .ToArray();
        var normalScenarios = scenarios
            .Where(scenario => !scenario.ScenarioId.EndsWith(".production_access_unavailable", StringComparison.Ordinal))
            .ToArray();

        return scenarios.Count == 22
            && normalScenarios.Length == 20
            && normalScenarios.All(scenario => scenario.Passed
                && scenario.RawFree
                && scenario.CleanupPassed
                && scenario.Status == "passed"
                && scenario.EvidenceLevel == ReferenceComposerEvidenceLevelTokens.RequiredToken)
            && negativeControls.Length == 2
            && negativeControls.Select(scenario => scenario.ScenarioId).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(new[]
                {
                    "run1.production_access_unavailable",
                    "run2.production_access_unavailable"
                })
            && negativeControls.All(scenario => !scenario.Passed
                && scenario.Status == "expected_blocked"
                && scenario.RawFree
                && scenario.CleanupPassed
                && scenario.TerminalStatus == OsInteractionStatusIds.FailedClosed
                && scenario.ComposerAccess == "unavailable"
                && scenario.EvidenceLevel == ReferenceComposerEvidenceLevelTokens.UnavailableToken);
    }

    private static bool BlockedScenario(ReferenceComposerAcceptanceReport report)
    {
        return report.HookStarted
            && report.OriginalInputSuppressed
            && !report.Submitted
            && report.SentTexts.Count == 0
            && report.CleanupPassed
            && ProtectedSendTrace.IsValidTerminalTrace(report.Trace)
            && report.Trace.LastOrDefault()?.Stage == "terminal_blocked"
            && report.Trace.All(entry => entry.Stage != "sent_safely");
    }

    private static bool IsExpectedUnavailableProductionAccess(
        ReferenceComposerAcceptanceReport report,
        bool rawFree,
        string composerAccess,
        ReferenceComposerEvidenceLevel evidenceLevel)
    {
        return report.HookStarted
            && report.OriginalInputSuppressed
            && !report.Submitted
            && report.SentTexts.Count == 0
            && report.CleanupPassed
            && rawFree
            && report.Trace.LastOrDefault()?.ResultCode == OsInteractionStatusIds.FailedClosed
            && report.Trace.All(entry => entry.Stage is not ("text_written" or "replayed" or "send_injected" or "sent_safely"))
            && composerAccess == "unavailable"
            && evidenceLevel == ReferenceComposerEvidenceLevel.Unavailable;
    }

    private static bool IsRawFreeBlockedTerminal(IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        return trace.Count >= 1
            && trace.Count(entry => entry.Stage == "terminal_blocked") == 1
            && trace[^1].Stage == "terminal_blocked"
            && trace.All(entry => SafeStatus(entry.ResultCode) == entry.ResultCode);
    }

    private static bool IsCompleteCancellationTrace(IReadOnlyList<ProtectedSendTraceEntry> trace)
    {
        if (!ProtectedSendTrace.IsValidTerminalTrace(trace))
        {
            return false;
        }

        var stages = trace.Select(entry => entry.Stage).ToArray();
        return stages.SequenceEqual(new[]
        {
            "send_detected",
            "target_matched",
            "composer_read",
            "sanitized",
            "overlay_decision",
            "overlay_foreground_confirmed",
            "cancelled",
            "terminal_blocked"
        });
    }

    private static bool ReplayFailureScenario(
        ReferenceComposerAcceptanceReport report,
        string expectedTerminalStatus)
    {
        return BlockedScenario(report)
            && report.Trace.LastOrDefault()?.ResultCode == expectedTerminalStatus
            && report.Trace.All(entry => entry.Stage != "send_injected")
            && report.ReplayDiagnostics.TryGetValue("modifiers_released", out var released)
            && released == "true";
    }

    private static Sanitizer CreateSanitizer(byte[] hmacSecret)
    {
        return new Sanitizer(new InMemoryHmacMappingVault(hmacSecret));
    }

    private static string SanitizeForReference(byte[] hmacSecret, string prompt)
    {
        return CreateSanitizer(hmacSecret).Sanitize(new SanitizeRequest(
            new[] { new ContentPart("prompt", ContentSources.PromptText, prompt, new Dictionary<string, string>()) },
            new SanitizationContext(ReferenceOnlyInputSource.ProfileId, null, null, null, "default"),
            new SanitizationOptions(false, false, "reference-transaction"))).SanitizedText;
    }

    private static string SafeStatus(string value)
    {
        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.')
            ? value
            : "unavailable";
    }

    private static string CurrentBuildIdentifier()
        => typeof(ReferenceComposerReleaseAcceptanceRunner).Assembly.ManifestModule.ModuleVersionId
            .ToString("N");
}
