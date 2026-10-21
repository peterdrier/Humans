using Humans.Email.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Email.Services;
using Humans.Email.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;
using NSubstitute;

namespace Humans.Email.Tests.ViewComponents;

/// <summary>
/// The "send a message from {team}" buttons Email contributes to the profile page (Users
/// decomposition commit 7): who sees them and which teams they offer.
/// </summary>
public sealed class TeamMessageButtonsViewComponentTests
{
    private readonly ITeamMessageOptionsProvider _options = Substitute.For<ITeamMessageOptionsProvider>();
    private readonly ICommunicationPreferenceService _commPrefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();

    public TeamMessageButtonsViewComponentTests()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(true);
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>()).Returns(new UserInfo(
            _targetId, "Target", false, "en", null, Instant.FromUnixTimeSeconds(0),
            null, null, null, null, null, null, null,
            null, null, null, [], [], [], null, []));
    }

    [HumansFact]
    public void Parts_ContributeTheButtonsToTheProfileSlot()
    {
        IUserPart contribution = new SectionUserParts();

        contribution.Parts().Should().Contain(
            new UserPart(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), 200));
    }

    [HumansFact]
    public async Task OffersTheViewersTeams_SortedByName_OnAnotherMembersProfile()
    {
        var a = new TeamMessageOption(Guid.NewGuid(), "alpha", "a@nobodies.team");
        var b = new TeamMessageOption(Guid.NewGuid(), "Beta", "b@nobodies.team");
        _options.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([b, a]);

        var result = await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Public);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<TeamMessageButtonsViewModel>().Subject;
        model.UserId.Should().Be(_targetId);
        model.DisplayName.Should().Be("Target");
        model.Teams.Should().Equal(a, b);
    }

    [HumansFact]
    public async Task RendersNothing_OnTheViewersOwnProfile()
    {
        var result = await Build(_viewerId).InvokeAsync(_viewerId, ProfileCardViewMode.Self);

        AssertEmpty(result);
        await _options.DidNotReceiveWithAnyArgs().GetOptionsAsync(default, default);
    }

    [HumansFact]
    public async Task RendersNothing_WhenTheRecipientOptedOut_WithoutAskingForTeams()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Public);

        AssertEmpty(result);
        await _options.DidNotReceiveWithAnyArgs().GetOptionsAsync(default, default);
    }

    [HumansFact]
    public async Task RendersNothing_WhenTheViewerCoordinatesNoLinkedTeam()
    {
        _options.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([]);

        AssertEmpty(await Build(_viewerId).InvokeAsync(_targetId, ProfileCardViewMode.Admin));
    }

    [HumansFact]
    public async Task RendersNothing_ForAnAnonymousViewer()
    {
        AssertEmpty(await Build(null).InvokeAsync(_targetId, ProfileCardViewMode.Public));
        await _options.DidNotReceiveWithAnyArgs().GetOptionsAsync(default, default);
    }

    private static void AssertEmpty(Microsoft.AspNetCore.Mvc.IViewComponentResult result) =>
        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();

    private TeamMessageButtonsViewComponent Build(Guid? viewer)
    {
        var claims = viewer is { } id ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) } : [];
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
        return new TeamMessageButtonsViewComponent(_options, _commPrefs, _users)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } },
        };
    }
}
