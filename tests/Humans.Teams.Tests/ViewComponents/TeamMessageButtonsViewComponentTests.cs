using Xunit;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.AuditLog.Contracts;
using Humans.Google.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Teams.Services;
using Humans.Teams.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.Rendering;
using NSubstitute;

namespace Humans.Teams.Tests.ViewComponents;

/// <summary>
/// The "send a message from {team}" buttons Teams contributes to the profile page: who sees
/// them and which teams they offer.
/// </summary>
public sealed class TeamMessageButtonsViewComponentTests
{
    public enum ViewerAccount
    {
        Merged,
        Missing,
        Self,
        Cancelled
    }

    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly TeamMessageOptionsProvider _options;
    private readonly ICommunicationPreferenceService _commPrefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();

    public TeamMessageButtonsViewComponentTests()
    {
        _options = new TeamMessageOptionsProvider(_teams, _resources, _users, _commPrefs,
            Substitute.For<IEmailService>(), Substitute.For<IEmailMessageFactory>(), Substitute.For<IAuditLogService>(),
            NullLogger<TeamMessageOptionsProvider>.Instance);
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _targetId }, [], [], profile: null));
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _viewerId }, [], [], profile: null));
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(true);
    }

    [HumansTheory]
    [InlineData(ViewerAccount.Merged)]
    [InlineData(ViewerAccount.Missing)]
    [InlineData(ViewerAccount.Self)]
    [InlineData(ViewerAccount.Cancelled)]
    public async Task ViewerResolution_UsesTheSurvivorAndRefusesMissingOrSelf(ViewerAccount account)
    {
        using var request = new CancellationTokenSource();
        var survivorId = account == ViewerAccount.Self ? _targetId : Guid.NewGuid();
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == ViewerAccount.Missing
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], profile: null));
        });
        var option = new TeamMessageOption(Guid.NewGuid(), "Team", "team@example.com");
        StubOptions(survivorId, option);
        var component = Build(_viewerId);
        component.HttpContext.RequestAborted = request.Token;
        if (account == ViewerAccount.Cancelled)
        {
            await request.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => component.InvokeAsync(_targetId, ProfileCardViewMode.Public));
        }
        else
        {
            var result = await component.InvokeAsync(_targetId, ProfileCardViewMode.Public);
            if (account == ViewerAccount.Merged)
            {
                result.Should().BeOfType<ViewViewComponentResult>();
                await _teams.Received(1).GetTeamsAsync(request.Token);
            }
            else AssertEmpty(result);
        }
        if (account != ViewerAccount.Merged) _teams.ReceivedCalls().Should().BeEmpty();
        await _users.Received(1).GetUserInfoAsync(_viewerId, request.Token);
    }

    [HumansFact]
    public void Parts_ContributeTheButtonsToTheProfileSlot()
    {
        IUserPart contribution = ((IUserPart)new Section());

        contribution.Parts().Should().Contain(
            new UserPart(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), 200));
    }

    [HumansFact]
    public async Task OffersTheViewersTeams_SortedByName_OnAnotherMembersProfile()
    {
        var a = new TeamMessageOption(Guid.NewGuid(), "alpha", "a@nobodies.team");
        var b = new TeamMessageOption(Guid.NewGuid(), "Beta", "b@nobodies.team");
        StubOptions(_viewerId, b, a);

        var result = await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Public);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<TeamMessageButtonsViewModel>().Subject;
        model.UserId.Should().Be(_targetId);
        model.Teams.Should().Equal(a, b);
    }

    [HumansFact]
    public async Task RendersNothing_OnTheViewersOwnProfile()
    {
        var result = await Build(_viewerId).InvokeAsync(_viewerId, ProfileCardViewMode.Self);

        AssertEmpty(result);
        await _teams.DidNotReceiveWithAnyArgs().GetTeamsAsync(default);
    }

    [HumansFact]
    public async Task RendersNothing_WhenTheRecipientOptedOut_WithoutAskingForTeams()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Public);

        AssertEmpty(result);
        await _teams.DidNotReceiveWithAnyArgs().GetTeamsAsync(default);
    }

    [HumansFact]
    public async Task RendersNothing_WhenTheViewerCoordinatesNoLinkedTeam()
    {
        StubOptions(_viewerId);

        AssertEmpty(await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Admin));
    }

    [HumansFact]
    public async Task RendersNothing_ForAnAnonymousViewer()
    {
        AssertEmpty(await Build(null).InvokeAsync(_targetId, ProfileCardViewMode.Public));
        await _teams.DidNotReceiveWithAnyArgs().GetTeamsAsync(default);
    }

    private void StubOptions(Guid viewerId, params TeamMessageOption[] options)
    {
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(options.ToDictionary(t => t.TeamId, t =>
            new TeamInfo(t.TeamId, t.TeamName, null, t.TeamName, true, false, SystemTeamType.None,
                false, false, false, false, Instant.MinValue,
                [new TeamMemberInfo(Guid.NewGuid(), viewerId, "Viewer", null, null, TeamMemberRole.Coordinator, Instant.MinValue)],
                GoogleGroupPrefix: t.GoogleGroupEmail.Split('@')[0])));
        _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(options.ToDictionary(t => t.TeamId, t => (IReadOnlyList<GoogleResourceSnapshot>)
                [new GoogleResourceSnapshot(Guid.NewGuid(), t.TeamId, "123", t.TeamName, GoogleResourceType.Group,
                    $"https://groups.google.com/a/nobodies.team/g/{t.GoogleGroupEmail.Split('@')[0]}",
                    Instant.MinValue, Instant.MinValue, IsActive: true)]));
    }

    private static void AssertEmpty(Microsoft.AspNetCore.Mvc.IViewComponentResult result) =>
        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();

    private TeamMessageButtonsViewComponent Build(Guid? viewer)
    {
        var claims = viewer is { } id ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) } : [];
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
        return new TeamMessageButtonsViewComponent(_options)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } },
        };
    }
}
