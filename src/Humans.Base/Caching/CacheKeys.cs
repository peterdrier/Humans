namespace Humans.Base.Caching;

public static class CacheKeys
{
    public static string NotificationBadgeCounts(Guid userId) => $"NotificationBadge:{userId:N}";
    public const string NotificationMeters = "NotificationMeters";
    public const string HoldedContacts = "HoldedContacts";
    public const string SepaStuckBankLines = "SepaStuckBankLines";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";

    public static string RoleAssignmentClaims(Guid userId) => $"claims:{userId:N}";

    public static string ShiftAuthorization(Guid userId) => $"shift-auth:{userId:N}";

    public static string CampLeadJoinRequestsBadge(Guid userId) => $"NavBadge:CampLeadJoinRequests:{userId:N}";

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
            ["NotificationBadge"] = new("2 min", CacheKeyType.PerUser),
            ["NotificationMeters"] = new("2 min", CacheKeyType.Static),
            ["HoldedContacts"] = new("2 min", CacheKeyType.Static),
            ["SepaStuckBankLines"] = new("until the lines read again", CacheKeyType.Static),
            ["CampContactRateLimit"] = new("10 min", CacheKeyType.RateLimit),
            ["claims"] = new("60 sec", CacheKeyType.PerUser),
            ["shift-auth"] = new("60 sec", CacheKeyType.PerUser),
            ["NavBadge"] = new("2 min", CacheKeyType.PerUser),
            ["Legal"] = new("1 hour", CacheKeyType.PerEntity),
            ["magic_link_used"] = new("15 min", CacheKeyType.RateLimit),
            ["magic_link_signup"] = new("60 sec", CacheKeyType.RateLimit),
        };
}
