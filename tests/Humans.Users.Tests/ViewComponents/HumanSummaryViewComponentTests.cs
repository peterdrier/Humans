using Humans.Users.Tests.Infrastructure;
using Humans.Governance.Contracts;
using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Humans.Users.Models;
using Humans.Base.Enums;
using NodaTime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class HumanSummaryViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ProfiledHuman_UsesCanonicalPictureAndSummaryProjection()
    {
        var userId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var at = Instant.FromUtc(2026, 10, 22, 0, 0);
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            City = "Madrid",
            CountryCode = "ES",
            ProfilePictureContentType = "image/jpeg",
            UpdatedAt = at,
        };
        var info = new User
        {
            Id = userId,
            BurnerName = "Sparkle",
            State = UserState.Suspended,
            PreferredLanguage = "es",
        }.ToUserInfo(profile: profile);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>()).Returns(info);
        var teams = Substitute.For<ITeamService>();
        TeamInfo Team(string name, bool hidden = false, bool active = true, SystemTeamType system = SystemTeamType.None) =>
            new(Guid.NewGuid(), name, null, name, active, system != SystemTeamType.None, system,
                false, false, hidden, false, at,
                [new TeamMemberInfo(Guid.NewGuid(), userId, "Sparkle", null, null, TeamMemberRole.Member, at)]);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new[] { Team("Zulu"), Team("Hidden", hidden: true), Team("Alpha"),
                Team("Inactive", active: false), Team("Volunteers", system: SystemTeamType.Volunteers) }
                .ToDictionary(t => t.Id));
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(userId, Arg.Any<CancellationToken>()).Returns(MembershipTier.Asociado);
        var component = new HumanSummaryViewComponent(users, teams, governance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext(),
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var result = await component.InvokeAsync(requestedId);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData?.Model;
        var vm = model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.UserId.Should().Be(userId);
        vm.DisplayName.Should().Be("Sparkle");
        vm.ProfilePictureUrl.Should().Be($"/Profile/Picture?id={profile.Id}&v={at.ToUnixTimeTicks()}");
        vm.MembershipTier.Should().Be("Asociado");
        vm.MembershipStatus.Should().Be("Suspended");
        vm.IsSuspended.Should().BeTrue();
        vm.City.Should().Be("Madrid");
        vm.Teams.Should().Equal("Alpha", "Zulu");
        vm.HiddenTeams.Should().Equal("Hidden");
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new HumanSummaryViewComponent(users, Substitute.For<ITeamService>(), Substitute.For<IGovernanceService>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
