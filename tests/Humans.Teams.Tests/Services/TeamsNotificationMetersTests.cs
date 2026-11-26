using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Teams.Tests.Services;

public sealed class TeamsNotificationMetersTests : IDisposable
{
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly TeamsNotificationMeters _sut;

    public TeamsNotificationMetersTests() =>
        _sut = new TeamsNotificationMeters(_teams, _localization.GetRequiredService<IStringLocalizer<TeamsResource>>());

    public void Dispose() => _localization.Dispose();

    [HumansFact]
    public async Task Admin_SeesPendingRequestsSummedAcrossTeams_ReadLiveEachTime()
    {
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Teams(3, 4), Teams(1));
        var admin = Viewer(RoleNames.Admin);
        var ct = TestContext.Current.CancellationToken;

        var first = await _sut.GetMetersAsync(admin, Guid.NewGuid(), ct);
        var second = await _sut.GetMetersAsync(admin, Guid.NewGuid(), ct);

        var meter = first.Should().ContainSingle().Subject;
        meter.Title.Should().Be("Team join requests pending");
        meter.Count.Should().Be(7);
        meter.ActionUrl.Should().Be("/Teams/Summary");
        meter.Audience.Should().Be("Admins");
        meter.Priority.Should().Be(5);
        second.Should().ContainSingle().Which.Count.Should().Be(1);
    }

    [HumansFact]
    public async Task NonAdmin_GetsNoMeter()
    {
        var meters = await _sut.GetMetersAsync(Viewer(RoleNames.Board), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
        await _teams.DidNotReceiveWithAnyArgs().GetTeamsAsync();
    }

    private static ClaimsPrincipal Viewer(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    private static IReadOnlyDictionary<Guid, TeamInfo> Teams(params int[] pending) =>
        pending.Select(count => new TeamInfo(
                Id: Guid.NewGuid(), Name: "T", Description: null, Slug: "t",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: true, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                Members: [], PendingRequestCount: count))
            .ToDictionary(t => t.Id);
}
