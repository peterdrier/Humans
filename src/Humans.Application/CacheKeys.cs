namespace Humans.Application;

public static class CacheKeys
{
    public const string NavBadgeCounts = "NavBadgeCounts";

    public static string NotificationBadgeCounts(Guid userId) => $"NotificationBadge:{userId:N}";
    public const string NotificationMeters = "NotificationMeters";
    public const string ActiveTeams = "ActiveTeams";
    public const string CampSettings = "CampSettings";

    public static string TicketEventSummary(string eventId) => $"TicketEventSummary:{eventId}";

    public static string CampSeasonsByYear(int year) => $"camps_year_{year}";

    public static string UserTicketCount(Guid userId) => $"UserTicketCount:{userId:N}";
    public const string TicketDashboardStats = "TicketDashboardStats";
    public const string UserIdsWithTickets = "UserIdsWithTickets";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";

    public static string RoleAssignmentClaims(Guid userId) => $"claims:{userId:N}";

    public static string ShiftAuthorization(Guid userId) => $"shift-auth:{userId:N}";

    public static string VotingBadge(Guid userId) => $"NavBadge:Voting:{userId:N}";

    public static string LegalDocument(string slug) => $"Legal:{slug}";
    public const string NobodiesTeamEmails = "NobodiesTeamEmails_All";

    // Magic link sentinel keys (rate limiting and replay prevention)
    public static string MagicLinkUsed(string tokenPrefix) => $"magic_link_used:{tokenPrefix}";
    public static string MagicLinkSignupRateLimit(string normalizedEmail) => $"magic_link_signup:{normalizedEmail}";

    /// <summary>
    /// Cache key type classification for the Admin Cache Stats page.
    /// </summary>
    public enum CacheKeyType
    {
        Static,
        PerUser,
        PerEntity,
        RateLimit
    }

    /// <summary>
    /// Metadata for a cache key prefix: configured TTL and key type.
    /// </summary>
    public record CacheKeyMeta(string Ttl, CacheKeyType Type);

    /// <summary>
    /// Static lookup of known cache key prefixes to their TTL and type classification.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CacheKeyMeta> Metadata =
        new Dictionary<string, CacheKeyMeta>(StringComparer.Ordinal)
        {
            ["NavBadgeCounts"] = new("2 min", CacheKeyType.Static),
            ["NotificationBadge"] = new("2 min", CacheKeyType.PerUser),
            ["NotificationMeters"] = new("2 min", CacheKeyType.Static),
            ["ActiveTeams"] = new("10 min", CacheKeyType.Static),
            ["CampSettings"] = new("5 min", CacheKeyType.Static),
            ["TicketEventSummary"] = new("15 min", CacheKeyType.PerEntity),
            ["camps_year"] = new("5 min", CacheKeyType.PerEntity),
            ["UserTicketCount"] = new("5 min", CacheKeyType.PerUser),
            ["TicketDashboardStats"] = new("5 min", CacheKeyType.Static),
            ["UserIdsWithTickets"] = new("5 min", CacheKeyType.Static),
            ["CampContactRateLimit"] = new("10 min", CacheKeyType.RateLimit),
            ["claims"] = new("60 sec", CacheKeyType.PerUser),
            ["shift-auth"] = new("60 sec", CacheKeyType.PerUser),
            ["NavBadge"] = new("2 min", CacheKeyType.PerUser),
            ["Legal"] = new("1 hour", CacheKeyType.PerEntity),
            ["magic_link_used"] = new("15 min", CacheKeyType.RateLimit),
            ["magic_link_signup"] = new("60 sec", CacheKeyType.RateLimit),
            [NobodiesTeamEmails] = new("2 min", CacheKeyType.Static),
        };
}
