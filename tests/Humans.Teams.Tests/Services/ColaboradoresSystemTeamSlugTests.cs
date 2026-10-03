using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The Colaboradores system team is seeded with its canonical slug and the
/// legacy misspelled slug as a CustomSlug alias (nobodies-collective/Humans#1755).
/// </summary>
public sealed class ColaboradoresSystemTeamSlugTests
{
    [HumansTheory]
    [InlineData("colaboradores")]
    [InlineData("colaboradors")]
    public async Task SeededTeam_ResolvesByCanonicalAndLegacySlug(string slug)
    {
        var options = new DbContextOptionsBuilder<TeamsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TeamsDbContext(options);
        await db.Database.EnsureCreatedAsync(Xunit.TestContext.Current.CancellationToken);

        var team = await db.Teams.SingleAsync(
            t => t.Id == SystemTeamIds.Colaboradores, Xunit.TestContext.Current.CancellationToken);
        var info = new TeamInfo(
            team.Id, team.Name, team.Description, team.Slug, team.IsActive, IsSystemTeam: true,
            team.SystemTeamType, team.RequiresApproval, team.IsPublicPage, team.IsHidden,
            team.IsPromotedToDirectory, team.CreatedAt, Members: [], CustomSlug: team.CustomSlug);

        team.Name.Should().Be("Colaboradores");
        team.SystemTeamType.Should().Be(SystemTeamType.Colaboradores);
        TeamSlugResolver.Find(new Dictionary<Guid, TeamInfo> { [team.Id] = info }, slug)
            .Should().NotBeNull().And.Subject.As<TeamInfo>().Id.Should().Be(SystemTeamIds.Colaboradores);
    }
}
