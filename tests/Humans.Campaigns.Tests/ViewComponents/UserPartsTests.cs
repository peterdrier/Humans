using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Humans.Campaigns.Tests.ViewComponents;

/// <summary>
/// The "My Codes" and "Campaign Codes" cards, contributed to the profile and admin-detail
/// slots: who sees them, and what they show.
/// </summary>
public class UserPartsTests
{
    private readonly ICampaignService _campaigns = Substitute.For<ICampaignService>();
    private readonly Guid _userId = Guid.NewGuid();

    private CampaignGrantSummary Grant(string title) => new(
        Guid.NewGuid(), Guid.NewGuid(), title, Guid.NewGuid(), "CODE-" + title, _userId,
        Instant.FromUtc(2026, 1, 1, 0, 0), null);

    [HumansFact]
    public void Parts_ContributeMyCodesToProfile_AndCampaignCodesToAdminDetail()
    {
        IUserPart contribution = ((IUserPart)new Section());

        contribution.Parts().Should().BeEquivalentTo(
        [
            new UserPart(UserPartSlots.Profile, typeof(MyCodesViewComponent), 100),
            new UserPart(UserPartSlots.AdminDetail, typeof(CampaignCodesViewComponent), 100),
        ]);
    }

    [HumansFact]
    public async Task MyCodes_ShowsTheViewersActiveOrCompletedGrants()
    {
        _campaigns.GetActiveOrCompletedGrantsForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([Grant("Summer")]);

        var result = await BuildMyCodes(viewerId: _userId).InvokeAsync(_userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeAssignableTo<IReadOnlyList<CampaignGrantSummary>>().Which.Should().ContainSingle();
        await _campaigns.DidNotReceive().GetAllGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MyCodes_RendersNothing_WhenThereAreNoGrants()
    {
        _campaigns.GetActiveOrCompletedGrantsForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await BuildMyCodes(viewerId: _userId).InvokeAsync(_userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Public)]
    [Xunit.InlineData(ProfileCardViewMode.Admin)]
    public async Task MyCodes_RendersNothing_OutsideTheOwnProfile(ProfileCardViewMode mode)
    {
        var result = await BuildMyCodes(viewerId: _userId).InvokeAsync(_userId, mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _campaigns.DidNotReceive().GetActiveOrCompletedGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MyCodes_RendersNothing_WhenTheViewerIsNotTheTarget()
    {
        var result = await BuildMyCodes(viewerId: Guid.NewGuid()).InvokeAsync(_userId, ProfileCardViewMode.Self);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _campaigns.DidNotReceive().GetActiveOrCompletedGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CampaignCodes_ShowsEveryGrantToAnAuthorizedAdminView()
    {
        _campaigns.GetAllGrantsForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([Grant("Draft"), Grant("Summer")]);

        var result = await BuildCampaignCodes(authorized: true).InvokeAsync(_userId, ProfileCardViewMode.Admin);

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeAssignableTo<IReadOnlyList<CampaignGrantSummary>>().Which.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task CampaignCodes_RendersNothing_WhenThereAreNoGrants()
    {
        _campaigns.GetAllGrantsForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await BuildCampaignCodes(authorized: true).InvokeAsync(_userId, ProfileCardViewMode.Admin);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(true, ProfileCardViewMode.Self)]
    [Xunit.InlineData(true, ProfileCardViewMode.Public)]
    [Xunit.InlineData(false, ProfileCardViewMode.Admin)]
    public async Task CampaignCodes_RendersNothing_ForANonAdminViewerOrAudience(bool authorized, ProfileCardViewMode mode)
    {
        var result = await BuildCampaignCodes(authorized).InvokeAsync(_userId, mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _campaigns.DidNotReceive().GetAllGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private MyCodesViewComponent BuildMyCodes(Guid viewerId) =>
        new(_campaigns) { ViewComponentContext = Context(viewerId) };

    private CampaignCodesViewComponent BuildCampaignCodes(bool authorized)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.HumanAdminBoardOrAdmin)
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return new CampaignCodesViewComponent(_campaigns, authorization) { ViewComponentContext = Context(Guid.NewGuid()) };
    }

    private static ViewComponentContext Context(Guid viewerId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, viewerId.ToString())], "TestAuth")),
        };
        return new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } };
    }
}
