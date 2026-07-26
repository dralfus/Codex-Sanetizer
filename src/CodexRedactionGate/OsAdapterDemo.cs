using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public sealed record OsAdapterDemoReport(
    bool Passed,
    bool DryRunPassed,
    bool ApplyOnlyPassed,
    bool ConfirmAndSendDisabledByDefaultPassed,
    bool ConfirmAndSendPassed,
    bool CancelPassed,
    bool BlockPassed,
    bool WriteFailurePassed,
    bool AuditRawFreePassed);

public static class OsAdapterDemoRunner
{
    public static OsInteractionResult RunDryRun(ISanitizer sanitizer, string prompt)
    {
        var surface = new DemoTextSurface(prompt);
        var orchestrator = new OsInteractionOrchestrator(
            sanitizer,
            surface,
            surface,
            surface,
            surface,
            new DemoConfirmationOverlay(ConfirmationDecisionContract.Confirm));

        return orchestrator.RunOnce(OsInteractionRunOptions.DryRunOnly);
    }

    public static OsAdapterDemoReport RunSmoke(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var audit = new InMemoryOsAdapterAuditSink();

        var dryRun = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.DryRunOnly, ConfirmationDecisionContract.Confirm);
        var applyOnly = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.ApplyOnly, ConfirmationDecisionContract.Confirm);
        var sendDisabled = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.ApplyOnly, ConfirmationDecisionContract.Confirm);
        var send = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.ConfirmAndSend, ConfirmationDecisionContract.Confirm);
        var cancel = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.ConfirmAndSend, ConfirmationDecisionContract.Cancel);
        var block = RunCase("Reject BLOCK_THIS", OsInteractionRunOptions.ConfirmAndSend, ConfirmationDecisionContract.Confirm);
        var writeFailure = RunCase("Connect to 192.168.10.25", OsInteractionRunOptions.ApplyOnly, ConfirmationDecisionContract.Confirm, failWrites: true);

        foreach (var result in new[] { dryRun, applyOnly, sendDisabled, send, cancel, block, writeFailure })
        {
            audit.Write(OsAdapterAudit.FromResult(result));
        }

        var serializedAudit = System.Text.Json.JsonSerializer.Serialize(audit.Events);
        var auditRawFree = !serializedAudit.Contains("192.168.10.25", StringComparison.Ordinal)
            && !serializedAudit.Contains("BLOCK_THIS", StringComparison.Ordinal);

        var passed = dryRun.Status == OsInteractionStatusIds.DryRunConfirm
            && applyOnly.Status == OsInteractionStatusIds.Applied
            && applyOnly.Applied
            && !applyOnly.Submitted
            && sendDisabled.Status == OsInteractionStatusIds.Applied
            && !sendDisabled.Submitted
            && send.Status == OsInteractionStatusIds.Submitted
            && cancel.Status == OsInteractionStatusIds.Canceled
            && block.Status == OsInteractionStatusIds.Blocked
            && writeFailure.Status == OsInteractionStatusIds.WriteFailed
            && auditRawFree;

        return new OsAdapterDemoReport(
            Passed: passed,
            DryRunPassed: dryRun.Status == OsInteractionStatusIds.DryRunConfirm,
            ApplyOnlyPassed: applyOnly.Status == OsInteractionStatusIds.Applied && applyOnly.Applied && !applyOnly.Submitted,
            ConfirmAndSendDisabledByDefaultPassed: sendDisabled.Status == OsInteractionStatusIds.Applied && !sendDisabled.Submitted,
            ConfirmAndSendPassed: send.Status == OsInteractionStatusIds.Submitted,
            CancelPassed: cancel.Status == OsInteractionStatusIds.Canceled,
            BlockPassed: block.Status == OsInteractionStatusIds.Blocked,
            WriteFailurePassed: writeFailure.Status == OsInteractionStatusIds.WriteFailed,
            AuditRawFreePassed: auditRawFree);

        OsInteractionResult RunCase(
            string prompt,
            OsInteractionRunOptions options,
            Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory,
            bool failWrites = false)
        {
            var surface = new DemoTextSurface(prompt, failWrites);
            var orchestrator = new OsInteractionOrchestrator(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                surface,
                surface,
                surface,
                surface,
                new DemoConfirmationOverlay(decisionFactory));

            return orchestrator.RunOnce(options);
        }
    }

    private sealed class DemoConfirmationOverlay : IConfirmationOverlay
    {
        private readonly Func<ConfirmationUiModel, ConfirmationDecision> _decisionFactory;

        public DemoConfirmationOverlay(Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory)
        {
            _decisionFactory = decisionFactory;
        }

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            return _decisionFactory(model);
        }
    }

    private sealed class DemoTextSurface :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction
    {
        private readonly bool _failWrites;

        public DemoTextSurface(string currentText, bool failWrites = false)
        {
            CurrentText = currentText;
            _failWrites = failWrites;
            Surface = new TextSurfaceDescriptor(
                "demo-surface",
                "codex-desktop",
                "Codex Desktop Demo",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata(SurfaceKind: "demo"));
        }

        public string CurrentText { get; private set; }

        public TextSurfaceDescriptor Surface { get; }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            return TextSurfaceDiscoveryResult.Success(Surface);
        }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            return new TextCaptureResult(
                true,
                "captured",
                CurrentText,
                new Dictionary<string, string> { ["capture_length"] = CurrentText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            if (_failWrites)
            {
                return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
            }

            CurrentText = text;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string> { ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string> { ["submit_count"] = "1" });
        }
    }
}
