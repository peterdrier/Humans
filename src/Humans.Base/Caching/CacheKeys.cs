namespace Humans.Base.Caching;

public static class CacheKeys
{
    public const string HoldedContacts = "HoldedContacts";
    public const string SepaStuckBankLines = "SepaStuckBankLines";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";

    public static string LegalDocument(string slug) => $"Legal:{slug}";

    // Magic link sentinel keys (rate limiting and replay prevention)
    public static string MagicLinkUsed(string tokenDigest) => $"magic_link_used:{tokenDigest}";
    public static string MagicLinkSignupRateLimit(string normalizedEmail) => $"magic_link_signup:{normalizedEmail}";

    /// <summary>Classification for the Admin Cache Stats page.</summary>
    public enum CacheKeyType
    {
        Static,
        PerUser,
        PerEntity,
        RateLimit
    }

    /// <summary>TTL + type for a cache key prefix.</summary>
    public record CacheKeyMeta(string Ttl, CacheKeyType Type);

    /// <summary>Known cache key prefixes → TTL + type.</summary>
    public static readonly IReadOnlyDictionary<string, CacheKeyMeta> Metadata =
        new Dictionary<string, CacheKeyMeta>(StringComparer.Ordinal)
        {
            ["FeedbackBadgeCount"] = new("2 min", CacheKeyType.Static),
            ["NotificationMeters"] = new("2 min", CacheKeyType.Static),
            ["HoldedContacts"] = new("2 min", CacheKeyType.Static),
            ["SepaStuckBankLines"] = new("until the lines read again", CacheKeyType.Static),
            ["CampContactRateLimit"] = new("10 min", CacheKeyType.RateLimit),
            ["NavBadge"] = new("2 min", CacheKeyType.PerUser),
            ["Legal"] = new("1 hour", CacheKeyType.PerEntity),
            ["magic_link_used"] = new("15 min", CacheKeyType.RateLimit),
            ["magic_link_signup"] = new("60 sec", CacheKeyType.RateLimit),
        };
}
