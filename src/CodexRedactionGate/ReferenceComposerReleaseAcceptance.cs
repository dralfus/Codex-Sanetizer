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
    string ComposerAccess);

internal sealed record ReferenceComposerReleaseAcceptanceReport(
    string Status,
    bool InteractiveDesktopAvailable,
    IReadOnlyList<ReferenceComposerReleaseScenarioResult> Scenarios,
    bool CleanupPassed)
{
    public bool Passed => Status == "passed"
        && InteractiveDesktopAvailable
        && CleanupPassed
        && Scenarios.Count > 0
        && Scenarios.All(scenario => scenario.Passed && scenario.RawFree && scenario.CleanupPassed);
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
                CleanupPassed: false);
        }
        if (!interactive)
        {
            return new ReferenceComposerReleaseAcceptanceReport(
                "interactive_desktop_unavailable",
                InteractiveDesktopAvailable: false,
                Array.Empty<ReferenceComposerReleaseScenarioResult>(),
                CleanupPassed: false);
        }

        var scenarios = new List<ReferenceComposerReleaseScenarioResult>();
        var firstRun = RunMatrix(hmacSecret, "run1");
        var secondRun = RunMatrix(hmacSecret, "run2");
        scenarios.AddRange(firstRun);
        scenarios.AddRange(secondRun);

        var cleanupPassed = scenarios.Count == 18
            && scenarios.All(scenario => scenario.CleanupPassed);
        return new ReferenceComposerReleaseAcceptanceReport(
            scenarios.All(scenario => scenario.Passed && scenario.RawFree) && cleanupPassed
                ? "passed"
                : "failed_closed",
            InteractiveDesktopAvailable: true,
            scenarios,
            cleanupPassed);
    }

    internal static IReadOnlyList<string> RenderRawFree(
        ReferenceComposerReleaseAcceptanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"status: {SafeStatus(report.Status)}",
            $"interactive_desktop: {report.InteractiveDesktopAvailable.ToString().ToLowerInvariant()}"
        };
        foreach (var scenario in report.Scenarios)
        {
            lines.Add(
                $"scenario: {SafeStatus(scenario.ScenarioId)} status: {SafeStatus(scenario.Status)} terminal_status: {SafeStatus(scenario.TerminalStatus)} composer_access: {SafeStatus(scenario.ComposerAccess)} raw_free: {scenario.RawFree.ToString().ToLowerInvariant()} cleanup: {scenario.CleanupPassed.ToString().ToLowerInvariant()}");
        }

        lines.Add($"cleanup: {report.CleanupPassed.ToString().ToLowerInvariant()}");
        lines.Add($"overall: {(report.Passed ? "passed" : "failed_closed")}");
        return lines;
    }

    private static IReadOnlyList<ReferenceComposerReleaseScenarioResult> RunMatrix(
        byte[] hmacSecret,
        string runId)
    {
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
            var scenarioPassed = passed(report);
            var rawFree = ProtectedSendTrace.IsValidTerminalTrace(report.Trace)
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
                ComposerAccess: composerAccess);
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
                ComposerAccess: "unavailable");
        }
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

    private static string SafeStatus(string value)
    {
        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.')
            ? value
            : "unavailable";
    }
}
