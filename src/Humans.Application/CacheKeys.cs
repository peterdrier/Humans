namespace Humans.Application;

public static class CacheKeys
{
    public const string NavBadgeCounts = "NavBadgeCounts";
    public const string ApprovedProfiles = "ApprovedProfiles";
    public const string ActiveTeams = "ActiveTeams";

    public static string CampContactRateLimit(Guid userId, Guid campId) =>
        $"CampContactRateLimit:{userId:N}:{campId:N}";
}
