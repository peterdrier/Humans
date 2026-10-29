using Humans.Teams.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Auth.ViewComponents;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Auth.Tests.ViewComponents;

/// <summary>
/// The "Role Assignments" card Auth contributes to the admin human-detail page: who sees it,
/// and what it shows.
/// </summary>
public sealed class RoleAssignmentsCardViewComponentTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 20, 12, 0);

    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly Guid _userId = Guid.NewGuid();

    [HumansFact]
    public void Parts_ContributeTheCardToTheAdminDetailSlot()
    {
        IUserPart contribution = ((IUserPart)new Section());

        contribution.Parts().Should().BeEquivalentTo(
            [new UserPart(UserPartSlots.AdminDetail, typeof(RoleAssignmentsCardViewComponent), 50)]);
    }

    [HumansFact]
    public async Task ShowsActiveAndEndedAssignments_ToAnAuthorizedAdminView()
    {
        var active = Summary(RoleNames.Board, validTo: null);
        var ended = Summary(TeamsRoles.TeamsAdmin, validTo: Now - Duration.FromDays(1));
        _roles.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns([active, ended]);

        var result = await Build(authorized: true).InvokeAsync(_userId, ProfileCardViewMode.Admin);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<RoleAssignmentsCardViewModel>().Subject;
        model.UserId.Should().Be(_userId);
        model.Roles.Select(r => (r.RoleName, r.IsActive))
            .Should().Equal((RoleNames.Board, true), (TeamsRoles.TeamsAdmin, false));
    }

    [HumansFact]
    public async Task StillRenders_ForAnAdmin_WhenTheMemberHoldsNoRole()
    {
        _roles.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await Build(authorized: true).InvokeAsync(_userId, ProfileCardViewMode.Admin);

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<RoleAssignmentsCardViewModel>().Which.Roles.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(true, ProfileCardViewMode.Self)]
    [Xunit.InlineData(true, ProfileCardViewMode.Public)]
    [Xunit.InlineData(false, ProfileCardViewMode.Admin)]
    public async Task RendersNothing_ForANonAdminViewerOrAudience(bool authorized, ProfileCardViewMode mode)
    {
        var result = await Build(authorized).InvokeAsync(_userId, mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _roles.DidNotReceive().GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private RoleAssignmentSummarySnapshot Summary(string role, Instant? validTo) => new(
        Guid.NewGuid(), _userId, "m@example.org", "Member", role,
        Now - Duration.FromDays(30), validTo, null, Guid.NewGuid(), "Actor", Now - Duration.FromDays(30));

    private RoleAssignmentsCardViewComponent Build(bool authorized)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.HumanAdminBoardOrAdmin)
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "TestAuth")),
        };
        return new RoleAssignmentsCardViewComponent(_roles, authorization, clock)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } },
        };
    }
}
