using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

internal static class TeamSlugResolver
{
    internal static TeamInfo? Find(IReadOnlyDictionary<Guid, TeamInfo> teamsById, string slug)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        return teamsById.Values.FirstOrDefault(t =>
            string.Equals(t.Slug, normalizedSlug, StringComparison.Ordinal) ||
            (t.CustomSlug is not null && string.Equals(t.CustomSlug, normalizedSlug, StringComparison.Ordinal)));
    }
}
