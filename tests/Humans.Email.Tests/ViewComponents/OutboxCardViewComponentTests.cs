using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Email.Services;
using Humans.Email.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Email.Tests.ViewComponents;

/// <summary>
/// The "Email Outbox" card Email contributes to the admin human-detail page (Users decomposition
/// commit 3): who sees it, and what it shows.
/// </summary>
public sealed class OutboxCardViewComponentTests
{
    private readonly IEmailOutboxService _outbox = Substitute.For<IEmailOutboxService>();
    private readonly Guid _userId = Guid.NewGuid();

    [HumansFact]
    public void Parts_ContributeTheCardToTheAdminDetailSlot()
    {
        IUserPart contribution = new SectionUserParts();

        contribution.Parts().Should().Contain(
            new UserPart(UserPartSlots.AdminDetail, typeof(OutboxCardViewComponent), 200));
    }

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(7)]
    public async Task ShowsTheMessageCount_ToAnAuthorizedAdminView_EvenAtZero(int count)
    {
        _outbox.GetMessageCountForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(count);

        var result = await Build(authorized: true).InvokeAsync(_userId, ProfileCardViewMode.Admin);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model;
        model.Should().BeOfType<OutboxCardViewModel>().Which.Should().Be(new OutboxCardViewModel(_userId, count));
    }

    [HumansTheory]
    [Xunit.InlineData(true, ProfileCardViewMode.Self)]
    [Xunit.InlineData(true, ProfileCardViewMode.Public)]
    [Xunit.InlineData(false, ProfileCardViewMode.Admin)]
    public async Task RendersNothing_ForANonAdminViewerOrAudience(bool authorized, ProfileCardViewMode mode)
    {
        var result = await Build(authorized).InvokeAsync(_userId, mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _outbox.DidNotReceive().GetMessageCountForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private OutboxCardViewComponent Build(bool authorized)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.HumanAdminBoardOrAdmin)
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "TestAuth")),
        };
        return new OutboxCardViewComponent(_outbox, authorization)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } },
        };
    }
}
