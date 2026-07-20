using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexRedactionGate;

public sealed record GitleaksProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string StandardInput);

public sealed record GitleaksProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public sealed record GitleaksScanResult(
    string ScannerStatus,
    string FindingsJson,
    bool TimedOut);

public sealed record SecretScanResult(
    bool TimedOut,
    string ScannerStatus,
    IReadOnlyList<GitleaksFindingSpan> Findings);

public interface IGitleaksProcessRunner
{
    GitleaksProcessResult Run(GitleaksProcessRequest request, TimeSpan timeout);
}

public interface ISecretScanner
{
    SecretScanResult Scan(string input, TimeSpan timeout);
}

public sealed class GitleaksPipeAdapter
{
    private readonly IGitleaksProcessRunner _runner;

    public GitleaksPipeAdapter()
        : this(new ProcessGitleaksRunner())
    {
    }

    public GitleaksPipeAdapter(IGitleaksProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public GitleaksScanResult Scan(string input, string executablePath, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var request = new GitleaksProcessRequest(
            ExecutablePath: executablePath,
            Arguments: new[]
            {
                "detect",
                "--no-git",
                "--source",
                "-",
                "--report-format",
                "json",
                "--report-path",
                "-",
                "--redact"
            },
            StandardInput: input);
        var result = _runner.Run(request, timeout);

        if (result.TimedOut)
        {
            return new GitleaksScanResult("timeout", string.Empty, TimedOut: true);
        }

        var findingsJson = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "[]"
            : result.StandardOutput.Trim();
        if (!IsValidJsonArray(findingsJson))
        {
            return new GitleaksScanResult("invalid_json", findingsJson, TimedOut: false);
        }

        if (result.ExitCode is not (0 or 1))
        {
            return new GitleaksScanResult("scanner_error", findingsJson, TimedOut: false);
        }

        var scannerStatus = findingsJson == "[]"
            ? "no_findings"
            : "findings";

        return new GitleaksScanResult(scannerStatus, findingsJson, TimedOut: false);
    }

    private static bool IsValidJsonArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class GitleaksSecretScanner : ISecretScanner
{
    private readonly GitleaksPipeAdapter _adapter;
    private readonly string _executablePath;

    public GitleaksSecretScanner(GitleaksPipeAdapter adapter, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _adapter = adapter;
        _executablePath = executablePath;
    }

    public SecretScanResult Scan(string input, TimeSpan timeout)
    {
        var result = _adapter.Scan(input, _executablePath, timeout);
        if (result.TimedOut || result.ScannerStatus is "invalid_json" or "scanner_error")
        {
            return new SecretScanResult(
                TimedOut: result.TimedOut,
                ScannerStatus: result.ScannerStatus,
                Findings: Array.Empty<GitleaksFindingSpan>());
        }

        return new SecretScanResult(
            TimedOut: false,
            ScannerStatus: result.ScannerStatus,
            Findings: GitleaksFindingConverter.Convert(input, result.FindingsJson));
    }
}

public sealed class ProcessGitleaksRunner : IGitleaksProcessRunner
{
    public GitleaksProcessResult Run(GitleaksProcessRequest request, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(request.StandardInput);
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return new GitleaksProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new GitleaksProcessResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult(),
            TimedOut: false);
    }
}
