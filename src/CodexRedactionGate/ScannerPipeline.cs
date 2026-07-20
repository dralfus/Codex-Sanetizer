using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class PolicyBlockEvaluator
{
    private readonly RedactionPolicy _policy;

    public PolicyBlockEvaluator(RedactionPolicy policy)
    {
        _policy = policy;
    }

    public PolicyRule? FindMatchingBlockRule(string text)
    {
        foreach (var rule in _policy.BlockRules)
        {
            if (rule.Pattern is null)
            {
                continue;
            }

            if (Regex.IsMatch(
                text,
                rule.Pattern,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)))
            {
                return rule;
            }
        }

        return null;
    }
}

internal sealed class ExternalScannerOrchestrator
{
    private readonly ISecretScanner? _secretScanner;
    private readonly RedactionPolicy _policy;

    public ExternalScannerOrchestrator(ISecretScanner? secretScanner, RedactionPolicy policy)
    {
        _secretScanner = secretScanner;
        _policy = policy;
    }

    public SecretScanResult? Run(string text, Stopwatch stopwatch)
    {
        if (_secretScanner is null)
        {
            return null;
        }

        var configuredBudgetMs = Math.Min(
            _policy.ScannerSettings.GitleaksTimeoutMs,
            (int)Sanitizer.GitleaksBudgetCap.TotalMilliseconds);
        var remainingBudgetMs = Math.Max(
            0,
            (int)(Sanitizer.TotalHardCap.TotalMilliseconds - stopwatch.ElapsedMilliseconds));
        var scannerBudgetMs = Math.Min(configuredBudgetMs, remainingBudgetMs);

        if (scannerBudgetMs <= 0)
        {
            return new SecretScanResult(
                TimedOut: true,
                ScannerStatus: ScannerStatusIds.Timeout.Value,
                Findings: Array.Empty<GitleaksFindingSpan>());
        }

        return _secretScanner.Scan(text, TimeSpan.FromMilliseconds(scannerBudgetMs));
    }

    public static bool IsFatal(SecretScanResult scannerResult)
    {
        return scannerResult.TimedOut
            || ScannerStatusIds.FromPublic(scannerResult.ScannerStatus).IsFatal();
    }
}
