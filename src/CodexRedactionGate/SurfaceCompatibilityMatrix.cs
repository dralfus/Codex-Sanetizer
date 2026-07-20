using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public sealed record SurfaceCompatibilityEntry(
    string ProfileId,
    string Application,
    string Channel,
    string SupportedScope,
    IReadOnlyList<string> RequiredEvidence,
    string Status);

public static class SurfaceCompatibilityMatrix
{
    public static IReadOnlyList<SurfaceCompatibilityEntry> SupportedV1 { get; } = new[]
    {
        new SurfaceCompatibilityEntry(
            ProfileId: "codex-desktop",
            Application: "Codex",
            Channel: "Windows desktop",
            SupportedScope: "focused-composer-only",
            RequiredEvidence: new[] { "read_only_diagnostic", "dry_run", "apply_only" },
            Status: "manual_verification_required"),
        new SurfaceCompatibilityEntry(
            ProfileId: "chatgpt-desktop",
            Application: "ChatGPT",
            Channel: "Windows desktop",
            SupportedScope: "focused-composer-only",
            RequiredEvidence: new[] { "read_only_diagnostic", "dry_run", "apply_only" },
            Status: "manual_verification_required")
    };

    public static IReadOnlyList<string> UnsupportedV1Scopes { get; } = new[]
    {
        "browser",
        "chrome",
        "pwa",
        "whole_window_capture"
    };

    public static IReadOnlyList<string> Render()
    {
        var lines = new List<string>
        {
            "compatibility_scope: windows_codex_chatgpt_desktop_only"
        };

        foreach (var entry in SupportedV1.OrderBy(entry => entry.ProfileId, StringComparer.Ordinal))
        {
            lines.Add(
                $"profile={entry.ProfileId} app={entry.Application} channel=\"{entry.Channel}\" scope={entry.SupportedScope} evidence={string.Join(",", entry.RequiredEvidence)} status={entry.Status}");
        }

        lines.Add($"unsupported_v1: {string.Join(",", UnsupportedV1Scopes)}");
        return lines;
    }
}
