using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodexRedactionGate;

public sealed record FixtureCorpusCase(
    string Name,
    SanitizeRequest Request,
    IReadOnlyList<DictionaryTerm> DictionaryTerms);

public sealed record FixtureCaseSummary(
    string Name,
    SanitizeDecision Decision,
    IReadOnlyList<string> ReplacementTypes,
    IReadOnlyList<string> ContentSources);

public sealed record FixtureCorpusReport(IReadOnlyList<FixtureCaseSummary> CaseSummaries)
{
    public IReadOnlySet<string> CoveredTypes { get; } = CaseSummaries
        .SelectMany(summary => summary.ReplacementTypes)
        .ToHashSet(StringComparer.Ordinal);

    public string RenderTextSummary()
    {
        var builder = new StringBuilder();

        foreach (var summary in CaseSummaries)
        {
            builder.Append(summary.Name);
            builder.Append(" decision=");
            builder.Append(summary.Decision);
            builder.Append(" types=");
            builder.AppendJoin(",", summary.ReplacementTypes);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

public static class FixtureCorpusRunner
{
    public static FixtureCorpusReport RunDefault(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var summaries = CreateDefaultCases()
            .Select(testCase =>
            {
                var sanitizer = new Sanitizer(
                    new InMemoryHmacMappingVault(hmacSecret),
                    testCase.DictionaryTerms);
                var result = sanitizer.Sanitize(testCase.Request);

                return new FixtureCaseSummary(
                    Name: testCase.Name,
                    Decision: result.Decision,
                    ReplacementTypes: result.Replacements
                        .Select(replacement => replacement.Type)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    ContentSources: testCase.Request.ContentParts
                        .Select(part => part.ContentSource)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            })
            .ToArray();

        return new FixtureCorpusReport(summaries);
    }

    public static IReadOnlyList<FixtureCorpusCase> CreateDefaultCases()
    {
        return new[]
        {
            CreateCase("internal-url-domain", "Use https://deploy.corp.example.local/api and deploy.corp.example.local"),
            CreateCase("private-ip-cidr", "Connect to 192.168.10.25 and route 10.20.30.0/24"),
            CreateCase("email-path", @"Send C:\Users\alexey.andreev\Documents\secret.txt to alexey.andreev@corp.example.local"),
            CreateCase("connection-string", "Use Server=db01.corp.example.local;Database=Billing;User Id=svc;Password=P@ssw0rd!"),
            CreateCase(
                "dictionary-term",
                "Talk to ACME Banking",
                new[]
                {
                    new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, "Known customer")
                }),
            CreateCase("gitleaks-shaped-token", "api_key=sk_live_1234567890abcdef"),
            new FixtureCorpusCase(
                Name: "text-attachment",
                Request: CreateRequest(new[]
                {
                    new ContentPart("prompt", ContentSources.PromptText, "Review attachment: ", new Dictionary<string, string>()),
                    new ContentPart(
                        "attachment-1",
                        ContentSources.TextAttachment,
                        "token=abcdef1234567890",
                        new Dictionary<string, string> { ["content_type"] = "text/plain" })
                }),
                DictionaryTerms: Array.Empty<DictionaryTerm>())
        };
    }

    private static FixtureCorpusCase CreateCase(
        string name,
        string prompt,
        IReadOnlyList<DictionaryTerm>? dictionaryTerms = null)
    {
        return new FixtureCorpusCase(
            Name: name,
            Request: CreateRequest(new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, prompt, new Dictionary<string, string>())
            }),
            DictionaryTerms: dictionaryTerms ?? Array.Empty<DictionaryTerm>());
    }

    private static SanitizeRequest CreateRequest(IReadOnlyList<ContentPart> contentParts)
    {
        return new SanitizeRequest(
            ContentParts: contentParts,
            Context: new SanitizationContext(
                Application: "fixture-corpus",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }
}
