using System;
using System.Diagnostics;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-scanner")]
public class SanitizerScannerPipelineTests
{
    [Test]
    public void ExternalSecretScan_UsesFiveSecondBudgetCap()
    {
        var scanner = new RecordingPipelineSecretScanner(new SecretScanResult(
            TimedOut: false,
            ScannerStatus: ScannerStatusIds.Ok.Value,
            Findings: Array.Empty<GitleaksFindingSpan>()));
        var orchestrator = new ExternalScannerOrchestrator(scanner, RedactionPolicy.BuiltInDefaults with
        {
            ScannerSettings = new PolicyScannerSettings(GitleaksEnabled: true, GitleaksTimeoutMs: 9000)
        });

        orchestrator.Run("safe text", Stopwatch.StartNew());

        Assert.That(scanner.LastTimeout, Is.EqualTo(Sanitizer.GitleaksBudgetCap));
    }

    [Test]
    public void InvalidExternalScannerJson_IsFatal()
    {
        var scannerResult = new SecretScanResult(
            TimedOut: false,
            ScannerStatus: ScannerStatusIds.InvalidJson.Value,
            Findings: Array.Empty<GitleaksFindingSpan>());

        Assert.That(ExternalScannerOrchestrator.IsFatal(scannerResult), Is.True);
    }

    private sealed class RecordingPipelineSecretScanner : ISecretScanner
    {
        private readonly SecretScanResult _result;

        public RecordingPipelineSecretScanner(SecretScanResult result)
        {
            _result = result;
        }

        public TimeSpan? LastTimeout { get; private set; }

        public SecretScanResult Scan(string input, TimeSpan timeout)
        {
            LastTimeout = timeout;
            return _result;
        }
    }
}
