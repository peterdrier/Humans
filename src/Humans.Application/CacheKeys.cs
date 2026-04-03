namespace Humans.Application;

public static class CacheKeys
{
    public const string NavBadgeCounts = "NavBadgeCounts";

    public static string NotificationBadgeCounts(Guid userId) => $"NotificationBadge:{userId:N}";
    public const string NotificationMeters = "NotificationMeters";
    public const string ApprovedProfiles = "ApprovedProfiles";
    public const string ActiveTeams = "ActiveTeams";
    public const string CampSettings = "CampSettings";

    public static string TicketEventSummary(string eventId) => $"TicketEventSummary:{eventId}";

    public static string CampSeasonsByYear(int year) => $"camps_year_{year}";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";

    public static string RoleAssignmentClaims(Guid userId) => $"claims:{userId:N}";

    public static string ShiftAuthorization(Guid userId) => $"shift-auth:{userId:N}";

    public static string LegalDocument(string slug) => $"Legal:{slug}";

    // Magic link sentinel keys (rate limiting and replay prevention)
    public static string MagicLinkUsed(string tokenPrefix) => $"magic_link_used:{tokenPrefix}";
    public static string MagicLinkSignupRateLimit(string normalizedEmail) => $"magic_link_signup:{normalizedEmail}";
}
