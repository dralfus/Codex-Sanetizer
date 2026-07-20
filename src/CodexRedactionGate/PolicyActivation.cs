using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexRedactionGate;

public sealed record PolicyActivationResult(
    bool Activated,
    string Code,
    RedactionPolicy ActivePolicy);

public sealed record ManagedPolicyLoadResult(
    RedactionPolicy ActivePolicy,
    string Source,
    bool LoadedFromFile,
    bool Activated,
    IReadOnlyList<Warning> Warnings,
    EffectivePolicyReport Diagnostics);

public sealed class PolicyActivationStore
{
    private const string ActiveFileName = "active-policy.toml";
    private const string LastKnownGoodFileName = "last-known-good-policy.toml";
    private const string ActiveDictionaryFileName = "active-dictionary.csv";
    private const string LastKnownGoodDictionaryFileName = "last-known-good-dictionary.csv";

    private readonly string _policyDirectory;
    private readonly TomlPolicyLoader _loader = new();

    public PolicyActivationStore(string policyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDirectory);
        _policyDirectory = Path.GetFullPath(policyDirectory);
    }

    public static string ActivePolicyPath(string policyDirectory)
    {
        return Path.Combine(policyDirectory, ActiveFileName);
    }

    public static string LastKnownGoodPolicyPath(string policyDirectory)
    {
        return Path.Combine(policyDirectory, LastKnownGoodFileName);
    }

    public ManagedPolicyLoadResult LoadActivePolicyOrDefault()
    {
        Directory.CreateDirectory(_policyDirectory);

        var activePath = ActivePolicyPath(_policyDirectory);
        var lastKnownGoodPath = LastKnownGoodPolicyPath(_policyDirectory);
        var lastKnownGood = _loader.LoadOrDefault(lastKnownGoodPath);
        var active = _loader.LoadOrDefault(activePath, lastKnownGood.ActivePolicy);
        var source = ResolveSource(active, lastKnownGood);
        var diagnostics = PolicyPrecedenceReporter.Build(new[]
        {
            new PolicySource(source, active.ActivePolicy)
        });

        var usablePolicyActivated = active.Activated
            || source is "managed-last-known-good" or "built-in-defaults";

        return new ManagedPolicyLoadResult(
            active.ActivePolicy,
            source,
            active.LoadedFromFile,
            usablePolicyActivated,
            active.Warnings,
            diagnostics);
    }

    public PolicyActivationResult PromoteCandidate(string candidatePolicyText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePolicyText);
        Directory.CreateDirectory(_policyDirectory);

        var candidatePath = Path.Combine(_policyDirectory, $"candidate-{Guid.NewGuid():N}.toml");
        AtomicFileWriter.WriteAllBytes(candidatePath, Encoding.UTF8.GetBytes(candidatePolicyText));

        try
        {
            var lastKnownGood = LoadActivePolicyForMutationOrDefault();
            var result = _loader.LoadOrDefault(candidatePath, lastKnownGood);
            if (!result.Activated)
            {
                return new PolicyActivationResult(false, "candidate_policy_rejected", lastKnownGood);
            }

            var activePath = Path.Combine(_policyDirectory, ActiveFileName);
            if (File.Exists(activePath))
            {
                AtomicFileWriter.WriteAllBytes(Path.Combine(_policyDirectory, LastKnownGoodFileName), File.ReadAllBytes(activePath));
            }

            AtomicFileWriter.WriteAllBytes(activePath, Encoding.UTF8.GetBytes(candidatePolicyText));
            return new PolicyActivationResult(true, "policy_activated", result.ActivePolicy);
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    public PolicyActivationResult Rollback()
    {
        var previousPath = Path.Combine(_policyDirectory, LastKnownGoodFileName);
        if (!File.Exists(previousPath))
        {
            return new PolicyActivationResult(false, "rollback_policy_missing", LoadActivePolicyForMutationOrDefault());
        }

        var previousText = File.ReadAllText(previousPath);
        var previousPolicy = _loader.LoadOrDefault(previousPath);
        if (!previousPolicy.Activated)
        {
            return new PolicyActivationResult(false, "rollback_policy_invalid", LoadActivePolicyForMutationOrDefault());
        }

        AtomicFileWriter.WriteAllBytes(Path.Combine(_policyDirectory, ActiveFileName), Encoding.UTF8.GetBytes(previousText));
        return new PolicyActivationResult(true, "policy_rolled_back", previousPolicy.ActivePolicy);
    }

    public CsvDictionaryLoadResult PromoteDictionaryCandidate(string candidateDictionaryCsv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateDictionaryCsv);
        Directory.CreateDirectory(_policyDirectory);

        var candidatePath = Path.Combine(_policyDirectory, $"candidate-{Guid.NewGuid():N}.csv");
        AtomicFileWriter.WriteAllBytes(candidatePath, Encoding.UTF8.GetBytes(candidateDictionaryCsv));

        try
        {
            var activePath = Path.Combine(_policyDirectory, ActiveDictionaryFileName);
            var lastKnownGood = new CsvDictionaryLoader().LoadOrDefault(activePath).ActiveTerms;
            var result = new CsvDictionaryLoader().LoadOrDefault(candidatePath, lastKnownGood);
            if (!result.Activated)
            {
                return result;
            }

            if (File.Exists(activePath))
            {
                AtomicFileWriter.WriteAllBytes(
                    Path.Combine(_policyDirectory, LastKnownGoodDictionaryFileName),
                    File.ReadAllBytes(activePath));
            }

            AtomicFileWriter.WriteAllBytes(activePath, Encoding.UTF8.GetBytes(candidateDictionaryCsv));
            return result;
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private RedactionPolicy LoadActivePolicyForMutationOrDefault()
    {
        return _loader.LoadOrDefault(Path.Combine(_policyDirectory, ActiveFileName)).ActivePolicy;
    }

    private static string ResolveSource(PolicyLoadResult active, PolicyLoadResult lastKnownGood)
    {
        if (active.LoadedFromFile && active.Activated)
        {
            return "managed-active";
        }

        if (active.LoadedFromFile
            && !active.Activated
            && lastKnownGood.LoadedFromFile
            && lastKnownGood.Activated)
        {
            return "managed-last-known-good";
        }

        return "built-in-defaults";
    }
}
