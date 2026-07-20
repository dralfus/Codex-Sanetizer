using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public static class SensitiveEntityTypes
{
    public const string Customer = "customer";
    public const string ConnectionString = "connection_string";
    public const string Domain = "domain";
    public const string Cidr = "cidr";
    public const string Email = "email";
    public const string FilePath = "file_path";
    public const string IpAddress = "ip_address";
    public const string Password = "password";
    public const string PrivateKey = "private_key";
    public const string Product = "product";
    public const string Project = "project";
    public const string Secret = "secret";
    public const string System = "system";
    public const string Token = "token";
    public const string Url = "url";
    public const string Username = "username";
    public const string SyntheticMarker = "synthetic_marker";
    public const string SyntheticBlockMarker = "synthetic_block_marker";

    private static readonly IReadOnlyDictionary<string, string> PrefixesByType = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Customer] = "CUSTOMER",
        [ConnectionString] = "CONNECTION",
        [Cidr] = "CIDR",
        [Domain] = "DOMAIN",
        [Email] = "EMAIL",
        [FilePath] = "PATH",
        [IpAddress] = "IP",
        [Product] = "PRODUCT",
        [Project] = "PROJECT",
        [System] = "SYSTEM",
        [Url] = "URL",
        [Username] = "USERNAME",
        [SyntheticMarker] = "SYNTHETIC",
        [SyntheticBlockMarker] = "SYNTHETIC_BLOCK"
    };

    public static IReadOnlyCollection<string> DictionaryTypes { get; } = new[]
    {
        Customer,
        Project,
        Product,
        Domain,
        System,
        Url,
        Username
    };

    public static bool IsSupportedDictionaryType(string entityType)
    {
        return DictionaryTypes.Contains(entityType);
    }

    public static bool TryGetPseudonymPrefix(string entityType, out string prefix)
    {
        return PrefixesByType.TryGetValue(entityType, out prefix!);
    }
}
