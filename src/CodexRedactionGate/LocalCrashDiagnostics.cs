using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace CodexRedactionGate;

/// <summary>
/// Crash report for local diagnostic capture
/// </summary>
public sealed record CrashReport(
    string ExceptionType,
    string ExceptionMessage,
    string StackTrace,
    string Component,
    string BuildVersion,
    DateTimeOffset Timestamp);

/// <summary>
/// Local crash diagnostics that captures application errors without leaking sensitive data
/// </summary>
public sealed class LocalCrashDiagnostics
{
    private readonly string _reportsDirectory;

    public LocalCrashDiagnostics(string reportsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportsDirectory);
        _reportsDirectory = reportsDirectory;
    }

    /// <summary>
    /// Capture a crash report without leaking raw prompt text or sensitive values
    /// </summary>
    public void Capture(Exception ex, string component)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);

        try
        {
            Directory.CreateDirectory(_reportsDirectory);
            
            var report = new CrashReport(
                ExceptionType: ex.GetType().FullName ?? ex.GetType().Name,
                ExceptionMessage: ex.Message,
                StackTrace: ex.StackTrace ?? string.Empty,
                Component: component,
                BuildVersion: GetBuildVersion(),
                Timestamp: DateTimeOffset.UtcNow);

            var reportPath = Path.Combine(
                _reportsDirectory,
                $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");

            // Write atomically to avoid corruption
            var tempPath = reportPath + ".tmp";
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, reportPath, overwrite: true);
        }
        catch
        {
            // Swallow any logging errors to avoid cascading failures
        }
    }

    /// <summary>
    /// Load crash reports without exposing raw sensitive data in diagnostics
    /// </summary>
    public IReadOnlyList<CrashReport> LoadReports()
    {
        var reports = new List<CrashReport>();

        if (!Directory.Exists(_reportsDirectory))
        {
            return reports;
        }

        try
        {
            foreach (var filePath in Directory.GetFiles(_reportsDirectory, "crash-*.json"))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var report = JsonSerializer.Deserialize<CrashReport>(json);
                    if (report is not null)
                    {
                        reports.Add(report);
                    }
                }
                catch
                {
                    // Skip corrupted reports
                }
            }
        }
        catch
        {
            // Skip directory access errors
        }

        return reports;
    }

    /// <summary>
    /// Get the latest crash report
    /// </summary>
    public CrashReport? GetLatestReport()
    {
        var reports = LoadReports();
        return reports.Count > 0 ? reports[0] : null;
    }

    /// <summary>
    /// Get crash report summary without raw prompt text or sensitive values
    /// </summary>
    public IReadOnlyList<string> GetRawFreeSummary()
    {
        var reports = LoadReports();
        var summary = new List<string>();

        foreach (var report in reports)
        {
            summary.Add($"[{report.Timestamp:O}] {report.Component} failed: {report.ExceptionType}");
            summary.Add($"  Message: {report.ExceptionMessage}");
            summary.Add($"  Build: {report.BuildVersion}");
        }

        return summary;
    }

    private static string GetBuildVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
