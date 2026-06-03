using AwesomeAssertions;
using Humans.Application.Services.Teams;
using Humans.Domain.Entities;
using Humans.Testing;
using NodaTime;

namespace Humans.Application.Tests.Services;

public class TeamEarlyEntryProjectionTests
{
    [HumansFact]
    public void Project_maps_each_grant_to_team_name_prefixed_source_and_preserves_date()
    {
        var u1 = Guid.NewGuid();
        var grants = new List<TeamEarlyEntryGrant>
        {
            new()
            {
                UserId = u1,
                EntryDate = new LocalDate(2026, 7, 3),
                ProjectName = "Flame Tower",
                Team = new Team { Name = "Creativity" },
            },
        };

        var result = TeamEarlyEntryProjection.Project(grants);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(u1);
        result[0].EntryDate.Should().Be(new LocalDate(2026, 7, 3));
        result[0].Source.Should().Be("Creativity: Flame Tower");
    }

    [HumansFact]
    public void Project_empty_input_returns_empty()
        => TeamEarlyEntryProjection.Project([]).Should().BeEmpty();
}
