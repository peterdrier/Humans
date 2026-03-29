namespace Humans.Application;

public static class CacheKeys
{
    public const string NavBadgeCounts = "NavBadgeCounts";
    public const string ApprovedProfiles = "ApprovedProfiles";
    public const string ActiveTeams = "ActiveTeams";
    public const string CampSettings = "CampSettings";
    public const string TicketEventSummary = "TicketEventSummary";

    public static string CampSeasonsByYear(int year) => $"camps_year_{year}";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";

    public static string RoleAssignmentClaims(Guid userId) => $"claims:{userId:N}";

    public static string ShiftAuthorization(Guid userId) => $"shift-auth:{userId:N}";

    public static string LegalDocument(string slug) => $"Legal:{slug}";
}
