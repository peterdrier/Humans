using System.Security.Claims;
using Humans.Governance.Controllers;
using Humans.Governance.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute.Core;
using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceIndexServiceTests
{
    private readonly IGovernanceService Applications = Substitute.For<IGovernanceService>();
    private readonly ILegalDocumentService LegalDocuments = Substitute.For<ILegalDocumentService>();
    private readonly IUserService Users = Substitute.For<IUserService>();
    private readonly IAssemblyVoteService Votes = Substitute.For<IAssemblyVoteService>();
    private readonly IRoleAssignmentService Roles = Substitute.For<IRoleAssignmentService>();

    private GovernanceIndexService CreateService()
    {
        LegalDocuments.GetDocumentContentAsync("statutes", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        Users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserInfo>)[]);
        return new GovernanceIndexService(Applications, LegalDocuments, Votes, Roles,
            new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 10, 17, 12, 0)));
    }

    [HumansFact]
    public async Task GetIndexDataAsync_CountsActiveTermsInsteadOfCachedProfileTiers()
    {
        var userId = Guid.NewGuid();
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Colaborador, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([userId]);
        Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([userId, Guid.NewGuid()]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ColaboradorCount.Should().Be(1);
        data.AsociadoCount.Should().Be(2);
        data.IsApprovedColaborador.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetIndexDataAsync_ForwardsCancellationToStatutesRead()
    {
        using var cancellation = new CancellationTokenSource();
        Applications.GetUserApplicationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await CreateService().GetIndexDataAsync(Guid.NewGuid(), cancellation.Token);

        await LegalDocuments.Received(1).GetDocumentContentAsync("statutes", cancellation.Token);
    }

    private static AssemblyVoteListItem Vote(AssemblyVoteStatus status, int closesOnDay) =>
        new(Guid.NewGuid(), $"{status} {closesOnDay}", status, AssemblyVoteKind.YesNo,
            Instant.FromUtc(2026, 11, closesOnDay, 12, 0), null, IsOnRoster: true, IsOfficial: true, HasBallot: false,
            new AssemblyVoteParticipation(0, 0, 0, 0, 0, 0, null));

    [HumansFact]
    public async Task GetIndexDataAsync_lists_only_open_votes_closing_soonest_first()
    {
        var userId = Guid.NewGuid();
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        var later = Vote(AssemblyVoteStatus.Open, 20);
        var sooner = Vote(AssemblyVoteStatus.Open, 10);
        Votes.GetVotesForMemberAsync(userId, Arg.Any<CancellationToken>())
            .Returns([later, Vote(AssemblyVoteStatus.Closed, 1), sooner, Vote(AssemblyVoteStatus.Cancelled, 5)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.OpenVotes.Should().Equal(sooner, later);
    }

    [HumansTheory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public async Task GetIndexDataAsync_counts_unvoted_applications_only_for_board_members(bool onBoard, int expected)
    {
        var userId = Guid.NewGuid();
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        Applications.GetUnvotedApplicationCountAsync(userId, Arg.Any<CancellationToken>()).Returns(3);
        Roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns(onBoard ? [userId, Guid.NewGuid()] : [Guid.NewGuid()]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.UnvotedApplicationCount.Should().Be(expected);
        data.BoardMemberIds.Should().HaveCount(onBoard ? 2 : 1);
    }

    private static UserApplicationSnapshot Approved(Guid userId, LocalDate? termExpiresAt) =>
        new(Guid.NewGuid(), userId, ApplicationStatus.Approved, MembershipTier.Colaborador,
            Instant.FromUtc(2026, 3, 1, 12, 0), Instant.FromUtc(2026, 3, 8, 12, 0),
            termExpiresAt, "motivation", null, null, null);

    [HumansFact]
    public async Task GetIndexDataAsync_carries_the_stored_term_expiry()
    {
        var userId = Guid.NewGuid();
        var stored = new LocalDate(2029, 12, 31);
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Approved(userId, stored)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ApplicationTermExpiresAt.Should().Be(stored);
    }

    [HumansFact]
    public async Task GetIndexDataAsync_leaves_the_term_expiry_null_when_none_is_stored()
    {
        var userId = Guid.NewGuid();
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Approved(userId, null)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ApplicationTermExpiresAt.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Index_CancelsAbandonedUserAndApplicationReads(bool cancelAfterViewer)
    {
        using var cancellation = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Member", Email = "member@example.com" }.ToUserInfo();
        var abandon = false;
        async ValueTask<UserInfo?> ReadUserAsync(CallInfo call)
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            if (abandon && cancelAfterViewer) await cancellation.CancelAsync();
            return user;
        }
        Users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(ReadUserAsync);
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UserApplicationSnapshot>>([]);
        });
        var controller = new GovernanceController(Users, CreateService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = cancellation.Token,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
                }
            },
        };

        (await controller.Index()).Should().BeOfType<ViewResult>();
        abandon = true;
        if (!cancelAfterViewer) await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index());
    }

}
