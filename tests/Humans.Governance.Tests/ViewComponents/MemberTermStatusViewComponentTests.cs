using Humans.Base.Constants;
using Humans.Base.Enums;
using NodaTime.Testing;
using Xunit;
using Humans.Teams.Contracts;
using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Governance.Tests.ViewComponents;

public sealed class MemberTermStatusViewComponentTests
{
    [HumansTheory]
    [InlineData(-1, false, true)]
    [InlineData(0, true, false)]
    [InlineData(90, true, false)]
    [InlineData(91, false, false)]
    public async Task InvokeAsync_UsesCurrentTierExpiryAndInclusiveRenewalWindow(int daysToExpiry, bool soon, bool expired)
    {
        var userId = Guid.NewGuid();
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var now = clock.GetCurrentInstant();
        var expiry = new LocalDate(2026, 3, 1).PlusDays(daysToExpiry);
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(userId, Arg.Any<CancellationToken>()).Returns(MembershipTier.Colaborador);
        var application = new UserApplicationSnapshot(Guid.NewGuid(), userId, ApplicationStatus.Approved,
            MembershipTier.Colaborador, now, now, expiry, "Motivation", null, null, null);
        governance.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns(
        [
            application with { Id = Guid.NewGuid(), TermExpiresAt = expiry.PlusDays(-100) },
            application,
            application with { Id = Guid.NewGuid(), MembershipTier = MembershipTier.Asociado, TermExpiresAt = expiry.PlusDays(100) },
            application with { Id = Guid.NewGuid(), Status = ApplicationStatus.Submitted, SubmittedAt = now.Plus(Duration.FromDays(1)), TermExpiresAt = null }
        ]);
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
            SystemTeamIds.Volunteers, "Volunteers", null, "volunteers", true, true, SystemTeamType.Volunteers,
            false, false, false, false, now,
            [new TeamMemberInfo(Guid.NewGuid(), userId, "Member", null, null, TeamMemberRole.Member, now)]));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var component = new MemberTermStatusViewComponent(teams, governance, clock)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } }
        };

        var result = await component.InvokeAsync();

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<MemberTermStatusViewModel>().Subject;
        model.TermExpiresAt.Should().Be(expiry.AtMidnight().InUtc().ToDateTimeUtc());
        model.TermExpiresSoon.Should().Be(soon);
        model.TermExpired.Should().Be(expired);
        model.HasPendingApplication.Should().BeTrue();
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => Task.FromException<MembershipTier>(new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var component = new MemberTermStatusViewComponent(
            Substitute.For<ITeamService>(), governance, SystemClock.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
