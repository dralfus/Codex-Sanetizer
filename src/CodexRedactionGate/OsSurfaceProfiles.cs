using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public sealed record SurfaceProfile(
    string ProfileId,
    string DisplayName,
    IReadOnlyList<string> WindowTitleTokens,
    IReadOnlyList<string> ProcessNameTokens,
    string ReadStrategy,
    string WriteStrategy,
    string SubmitStrategy);

public sealed record SurfaceProfileMatchResult(
    bool Matched,
    string Status,
    SurfaceProfile? Profile,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed class SurfaceProfileCatalog
{
    private static readonly string[] UnsupportedBrowserProcessTokens = { "chrome", "msedge", "firefox", "browser" };

    private readonly IReadOnlyList<SurfaceProfile> _profiles;

    public SurfaceProfileCatalog(IReadOnlyList<SurfaceProfile> profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public static SurfaceProfileCatalog Default { get; } = new(new[]
    {
        new SurfaceProfile(
            ProfileId: "redaction-gate-demo",
            DisplayName: "Redaction Gate Demo Target",
            WindowTitleTokens: new[] { "redaction gate demo target" },
            ProcessNameTokens: Array.Empty<string>(),
            ReadStrategy: "windows-ui-automation-value-pattern",
            WriteStrategy: "windows-ui-automation-value-pattern",
            SubmitStrategy: "disabled-demo-target"),
        new SurfaceProfile(
            ProfileId: "codex-desktop",
            DisplayName: "Codex Desktop",
            WindowTitleTokens: new[] { "codex" },
            ProcessNameTokens: new[] { "codex" },
            ReadStrategy: "windows-accessibility-or-clipboard",
            WriteStrategy: "windows-accessibility-or-clipboard",
            SubmitStrategy: "explicit-hotkey-send"),
        new SurfaceProfile(
            ProfileId: "chatgpt-desktop",
            DisplayName: "ChatGPT Desktop",
            WindowTitleTokens: new[] { "chatgpt", "chat gpt" },
            ProcessNameTokens: new[] { "chatgpt", "chat gpt" },
            ReadStrategy: "windows-accessibility-or-clipboard",
            WriteStrategy: "windows-accessibility-or-clipboard",
            SubmitStrategy: "explicit-hotkey-send")
    });

    public IReadOnlyList<SurfaceProfile> Profiles => _profiles;

    public SurfaceProfileMatchResult Match(string windowTitle, string processName)
    {
        if (IsUnsupportedBrowserProcess(processName))
        {
            return new SurfaceProfileMatchResult(
                false,
                OsInteractionStatusIds.UnsupportedSurface,
                null,
                new Dictionary<string, string>
                {
                    ["profile_match_count"] = "0",
                    ["unsupported_scope"] = "browser_or_pwa",
                    ["window_title_length"] = windowTitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["process_name_length"] = processName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        var matches = _profiles
            .Select(profile => new
            {
                Profile = profile,
                Score = MatchScore(profile, windowTitle, processName)
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ToArray();

        if (matches.Length == 0)
        {
            return new SurfaceProfileMatchResult(
                false,
                OsInteractionStatusIds.UnsupportedSurface,
                null,
                new Dictionary<string, string>
                {
                    ["profile_match_count"] = "0",
                    ["window_title_length"] = windowTitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["process_name_length"] = processName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        if (matches.Length > 1 && matches[0].Score == matches[1].Score)
        {
            return new SurfaceProfileMatchResult(
                false,
                OsInteractionStatusIds.AmbiguousSurface,
                null,
                new Dictionary<string, string>
                {
                    ["profile_match_count"] = matches.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        return new SurfaceProfileMatchResult(
            true,
            OsInteractionStatusIds.SupportedSurface,
            matches[0].Profile,
            new Dictionary<string, string>
            {
                ["profile_match_count"] = "1",
                ["profile_id"] = matches[0].Profile.ProfileId
            });
    }

    private static int MatchScore(SurfaceProfile profile, string windowTitle, string processName)
    {
        var titleScore = BestTokenLength(profile.WindowTitleTokens, windowTitle);
        if (titleScore > 0)
        {
            return 1000 + titleScore;
        }

        return BestTokenLength(profile.ProcessNameTokens, processName);
    }

    private static int BestTokenLength(IReadOnlyList<string> tokens, string value)
    {
        return tokens
            .Where(token => value.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Select(token => token.Length)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool IsUnsupportedBrowserProcess(string processName)
    {
        return UnsupportedBrowserProcessTokens.Any(token => processName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

}
