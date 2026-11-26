using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Governance.Contracts;
using Microsoft.Extensions.Localization;
using Humans.Governance.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.ViewComponents;

public sealed class MembershipTierBadgeViewComponentTests
{
    private static MembershipTierBadgeViewComponent Create(
        IGovernanceService governance, CancellationToken ct = default, bool isBoard = false)
    {
        var roles = Substitute.For<IRoleAssignmentService>();
        roles.IsUserBoardMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(isBoard);
        var localizer = Substitute.For<IStringLocalizer<GovernanceResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "Board"));
        return new(governance, roles, localizer)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestAborted = ct } }
            }
        };
    }

    [HumansFact]
    public async Task RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => Task.FromException<MembershipTier>(new OperationCanceledException(aborted.Token)));

        var act = async () => await Create(governance, aborted.Token).InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(MembershipTier.Volunteer, false)]
    [InlineData(MembershipTier.Colaborador, true)]
    [InlineData(MembershipTier.Asociado, true)]
    public async Task RendersOnlyForANonVolunteerTier(MembershipTier tier, bool renders)
    {
        var userId = Guid.NewGuid();
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(userId, Arg.Any<CancellationToken>()).Returns(tier);

        var result = await Create(governance).InvokeAsync(userId);

        if (renders)
            result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().Be(tier.ToString());
        else
            result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_board_member_shows_Board_over_their_tier()
    {
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MembershipTier.Asociado);

        var result = await Create(governance, isBoard: true).InvokeAsync(Guid.NewGuid());

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().Be("Board");
    }
}
