using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using NodaTime;

namespace Humans.Teams.Tests.Services;

public sealed class TeamSlugResolverTests
{
    private static readonly TeamInfo Team = new(Guid.NewGuid(), "Sound Camp", null, "sound-camp", true, false,
        SystemTeamType.None, false, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), [],
        CustomSlug: "sound");

    private static readonly IReadOnlyDictionary<Guid, TeamInfo> TeamsById = new Dictionary<Guid, TeamInfo>
    {
        [Team.Id] = Team,
    };

    [HumansTheory]
    [Xunit.InlineData("sound-camp")]
    [Xunit.InlineData("Sound-Camp")]
    [Xunit.InlineData("sound")]
    [Xunit.InlineData("SOUND")]
    public void Find_matches_slug_or_custom_slug_whatever_the_case(string slug) =>
        TeamSlugResolver.Find(TeamsById, slug).Should().BeSameAs(Team);

    [HumansFact]
    public void Find_returns_null_for_an_unknown_slug() =>
        TeamSlugResolver.Find(TeamsById, "lights").Should().BeNull();
}
