using System;
using System.Security.Cryptography;
using System.Text;

namespace CodexRedactionGate;

internal static class MappingPseudonyms
{
    public static string Create(byte[] hmacSecret, string entityType, string normalizedValue)
    {
        var hash = ComputeHash(hmacSecret, entityType, normalizedValue);
        if (string.Equals(entityType, SensitiveEntityTypes.Username, StringComparison.Ordinal))
        {
            return CreateReadableUsernamePseudonym(hash);
        }

        return CreateHexPseudonym(entityType, hash);
    }

    public static string CreateLegacyHex(byte[] hmacSecret, string entityType, string normalizedValue)
    {
        return CreateHexPseudonym(entityType, ComputeHash(hmacSecret, entityType, normalizedValue));
    }

    private static byte[] ComputeHash(byte[] hmacSecret, string entityType, string normalizedValue)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);

        using var hmac = new HMACSHA256(hmacSecret);
        var payload = Encoding.UTF8.GetBytes($"{entityType}\n{normalizedValue}");
        return hmac.ComputeHash(payload);
    }

    private static string CreateHexPseudonym(string entityType, byte[] hash)
    {
        return $"{GetPrefix(entityType)}_{Convert.ToHexString(hash, 0, 6)}";
    }

    private static string CreateReadableUsernamePseudonym(byte[] hash)
    {
        var adjective = UsernameAdjectives[ToIndex(hash[0], hash[1], UsernameAdjectives.Length)];
        var name = UsernameNames[ToIndex(hash[2], hash[3], UsernameNames.Length)];
        var suffix = Convert.ToHexString(hash, 4, 2);
        return $"{GetPrefix(SensitiveEntityTypes.Username)}_{adjective}_{name}_{suffix}";
    }

    private static int ToIndex(byte high, byte low, int length)
    {
        return ((high << 8) | low) % length;
    }

    private static readonly string[] UsernameAdjectives =
    {
        "brave",
        "calm",
        "clever",
        "curious",
        "eager",
        "gentle",
        "happy",
        "kind",
        "lively",
        "lucky",
        "merry",
        "nimble",
        "patient",
        "quiet",
        "rapid",
        "steady",
        "tidy",
        "witty",
        "bright",
        "polite",
        "sharp",
        "solid",
        "swift",
        "warm"
    };

    private static readonly string[] UsernameNames =
    {
        "ada",
        "babbage",
        "bohr",
        "curie",
        "darwin",
        "einstein",
        "faraday",
        "feynman",
        "franklin",
        "galileo",
        "hamilton",
        "hopper",
        "hypatia",
        "lovelace",
        "newton",
        "noether",
        "pasteur",
        "ramanujan",
        "shannon",
        "tesla",
        "turing",
        "volta",
        "watson",
        "wilson"
    };

    private static string GetPrefix(string entityType)
    {
        if (SensitiveEntityTypes.TryGetPseudonymPrefix(entityType, out var prefix))
        {
            return prefix;
        }

        throw new ArgumentException($"Unsupported entity type: {entityType}", nameof(entityType));
    }
}
