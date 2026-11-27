using System.Security.Claims;
using Humans.Governance.Controllers;
using Microsoft.AspNetCore.Authorization;
using Humans.Base.Authorization;
using Humans.Governance.Models;
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
using Humans.AuditLog.Contracts;
using Humans.Google.Contracts;
using Humans.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceIndexServiceTests
{
    private readonly IGovernanceManagement _applications = Substitute.For<IGovernanceManagement>();
    private readonly IApplicationRepository _tierApplications = Substitute.For<IApplicationRepository>();
    private readonly ILegalDocumentService _legalDocuments = Substitute.For<ILegalDocumentService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAssemblyVoteService _votes = Substitute.For<IAssemblyVoteService>();
    private readonly ISystemRoleService _roles = Substitute.For<ISystemRoleService>();
    private readonly HashSet<Guid> _inactive = [];
    private readonly GovernanceBodyRepository _bodies = new(new TestDbContextFactory<GovernanceDbContext>(
        new DbContextOptionsBuilder<GovernanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));

    private GovernanceIndexService CreateService()
    {
        var clock = new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 10, 17, 12, 0));
        _legalDocuments.GetDocumentContentAsync("statutes", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserInfo>)[]);
        _users.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult<IReadOnlyDictionary<Guid, UserInfo>>(call.Arg<IEnumerable<Guid>>().Distinct()
                .ToDictionary(id => id, id => UserInfo.Create(
                    new User { Id = id, BurnerName = "Member", CreatedAt = clock.GetCurrentInstant() },
                    [], [], _inactive.Contains(id) ? null : UserFixtures.Profile()))));
        return new GovernanceIndexService(_applications, _legalDocuments, _votes, _roles, _users, BodyLinks(),
            new GovernanceBodyRosters(_tierApplications, _roles, clock));
    }

    private Task StoreLinksAsync(GovernanceBody body, string prefix) =>
        _bodies.ReplaceAsync(new GovernanceBodyLinks
        {
            Body = body,
            GoogleGroupPrefix = prefix,
            Files = [new GovernanceBodyFile { Id = Guid.NewGuid(), Body = body, GoogleId = "f", Name = "Folder",
                ResourceType = GoogleResourceType.DriveFolder, PermissionLevel = DrivePermissionLevel.Viewer }],
        });

    [HumansTheory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Body_page_shows_links_only_to_members_and_board_or_admin(bool isMember, bool boardOrAdmin, bool expected)
    {
        var viewer = Guid.NewGuid();
        _users.GetUserInfoAsync(viewer, Arg.Any<CancellationToken>())
            .Returns(UserInfoFixtures.FromUser(new User { Id = viewer, DisplayName = "Viewer", Email = "viewer@example.com" }));
        _tierApplications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(isMember ? [viewer, Guid.NewGuid()] : [Guid.NewGuid()]);
        await StoreLinksAsync(GovernanceBody.Asociados, "asociados");
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.BoardOrAdmin)
            .Returns(boardOrAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var result = await CreateController(viewer, authorization).Body("Asociados");

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<GovernanceBodyViewModel>().Which;
        (model.Links is not null).Should().Be(expected);
        (model.Manage is not null).Should().Be(boardOrAdmin, "only Board and Admin manage a body's links");
        model.MemberIds.Should().HaveCount(isMember ? 2 : 1, "everyone signed in sees the members");
    }

    [HumansFact]
    public async Task A_member_who_is_not_Board_or_Admin_cannot_change_a_bodys_links()
    {
        var viewer = Guid.NewGuid();
        _users.GetUserInfoAsync(viewer, Arg.Any<CancellationToken>())
            .Returns(UserInfoFixtures.FromUser(new User { Id = viewer, DisplayName = "Viewer", Email = "viewer@example.com" }));
        await StoreLinksAsync(GovernanceBody.Asociados, "asociados");
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.BoardOrAdmin)
            .Returns(AuthorizationResult.Failed());
        var controller = CreateController(viewer, authorization);

        (await controller.SetGroup("Asociados", "taken-over")).Should().BeOfType<ForbidResult>();
        (await controller.AddFile("Asociados", "https://drive.google.com/drive/folders/x", DrivePermissionLevel.Manager))
            .Should().BeOfType<ForbidResult>();
        (await controller.RemoveFile("Asociados", Guid.NewGuid())).Should().BeOfType<ForbidResult>();

        (await _bodies.GetAllAsync(TestContext.Current.CancellationToken)).Single().GoogleGroupPrefix.Should().Be("asociados");
    }

    [HumansFact]
    public async Task A_board_member_is_a_member_since_the_earlier_of_their_seat_and_approval()
    {
        var viewer = Guid.NewGuid();
        var approved = Instant.FromUtc(2026, 9, 12, 0, 0);
        var seated = Instant.FromUtc(2024, 3, 1, 0, 0);
        _applications.GetUserApplicationsAsync(viewer, Arg.Any<CancellationToken>()).Returns([
            new UserApplicationSnapshot(Guid.NewGuid(), viewer, ApplicationStatus.Approved, MembershipTier.Asociado,
                approved, approved, new LocalDate(2027, 12, 31))]);
        _tierApplications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([viewer]);
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>()).Returns([viewer]);
        _roles.GetByUserIdAsync(viewer, Arg.Any<CancellationToken>()).Returns([
            new SystemRoleSummarySnapshot(Guid.NewGuid(), viewer, null, "Viewer", RoleNames.Board, seated, null, null,
                Guid.NewGuid(), null)]);

        var data = await CreateService().GetIndexDataAsync(viewer);

        data.CurrentTier.Should().Be(MembershipTier.Asociado);
        data.MemberSince.Should().Be(seated);
    }

    [HumansFact]
    public async Task Rosters_leave_out_inactive_accounts_and_your_access_lists_only_your_bodies()
    {
        var viewer = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _inactive.Add(gone);
        _applications.GetUserApplicationsAsync(viewer, Arg.Any<CancellationToken>()).Returns([]);
        _tierApplications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([viewer, gone]);
        await StoreLinksAsync(GovernanceBody.Asociados, "asociados");
        await StoreLinksAsync(GovernanceBody.Board, "board");

        var data = await CreateService().GetIndexDataAsync(viewer);

        data.AsociadoCount.Should().Be(1);
        data.YourAccess.Select(l => l.Body).Should().Equal(GovernanceBody.Asociados);
    }

    [HumansFact]
    public async Task GetIndexDataAsync_CountsActiveTermsInsteadOfCachedProfileTiers()
    {
        var userId = Guid.NewGuid();
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        _tierApplications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Colaborador, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([userId]);
        _tierApplications.GetActiveApprovedTierUserIdsAsync(
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
        _applications.GetUserApplicationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await CreateService().GetIndexDataAsync(Guid.NewGuid(), cancellation.Token);

        await _legalDocuments.Received(1).GetDocumentContentAsync("statutes", cancellation.Token);
    }

    private static AssemblyVoteListItem Vote(AssemblyVoteStatus status, int closesOnDay) =>
        new(Guid.NewGuid(), $"{status} {closesOnDay}", status, AssemblyVoteKind.YesNo,
            Instant.FromUtc(2026, 11, closesOnDay, 12, 0), null, IsOnRoster: true, IsOfficial: true, HasBallot: false,
            new AssemblyVoteParticipation(0, 0, 0, 0, 0, 0, null));

    [HumansFact]
    public async Task GetIndexDataAsync_lists_only_open_votes()
    {
        var userId = Guid.NewGuid();
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        var later = Vote(AssemblyVoteStatus.Open, 20);
        var sooner = Vote(AssemblyVoteStatus.Open, 10);
        _votes.GetVotesForMemberAsync(userId, Arg.Any<CancellationToken>())
            .Returns([later, Vote(AssemblyVoteStatus.Closed, 1), sooner, Vote(AssemblyVoteStatus.Cancelled, 5)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.OpenVotes.Should().BeEquivalentTo([later, sooner]);
    }

    [HumansTheory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public async Task GetIndexDataAsync_counts_unvoted_applications_only_for_board_members(bool onBoard, int expected)
    {
        var userId = Guid.NewGuid();
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        _applications.GetUnvotedApplicationCountAsync(userId, Arg.Any<CancellationToken>()).Returns(3);
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns(onBoard ? [userId, Guid.NewGuid()] : [Guid.NewGuid()]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.UnvotedApplicationCount.Should().Be(expected);
        data.BoardMemberIds.Should().HaveCount(onBoard ? 2 : 1);
    }

    private static UserApplicationSnapshot Approved(Guid userId, LocalDate? termExpiresAt) =>
        new(Guid.NewGuid(), userId, ApplicationStatus.Approved, MembershipTier.Colaborador,
            Instant.FromUtc(2026, 3, 1, 12, 0), Instant.FromUtc(2026, 3, 8, 12, 0),
            termExpiresAt);

    [HumansFact]
    public async Task GetIndexDataAsync_carries_the_stored_term_expiry()
    {
        var userId = Guid.NewGuid();
        var stored = new LocalDate(2029, 12, 31);
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Approved(userId, stored)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ApplicationTermExpiresAt.Should().Be(stored);
    }

    [HumansFact]
    public async Task GetIndexDataAsync_leaves_the_term_expiry_null_when_none_is_stored()
    {
        var userId = Guid.NewGuid();
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
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
        var user = UserInfoFixtures.FromUser(new User { Id = userId, DisplayName = "Member", Email = "member@example.com" });
        var abandon = false;
        async ValueTask<UserInfo?> ReadUserAsync(CallInfo call)
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            if (abandon && cancelAfterViewer) await cancellation.CancelAsync();
            return user;
        }
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(ReadUserAsync);
        _applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UserApplicationSnapshot>>([]);
        });
        var controller = CreateController(userId, Substitute.For<IAuthorizationService>(), cancellation.Token);

        (await controller.Index()).Should().BeOfType<ViewResult>();
        abandon = true;
        if (!cancelAfterViewer) await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index());
    }

    private GovernanceBodyLinksService BodyLinks() =>
        new(_bodies, Substitute.For<ITeamService>(), Substitute.For<ITeamResourceService>(), Substitute.For<IAuditLogService>(),
            new GovernanceBodySync(_bodies, Substitute.For<IGoogleGroupSync>(), Substitute.For<IGoogleSyncWrite>()),
            SystemClock.Instance);

    private GovernanceController CreateController(Guid userId, IAuthorizationService authorization, CancellationToken ct = default) =>
        new(_users, CreateService(),
            new GovernanceBoardSeats(_roles, Substitute.For<ISystemRoleWrite>(), _users, _bodies,
                Substitute.For<IAuditLogService>(),
                new GovernanceBodySync(_bodies, Substitute.For<IGoogleGroupSync>(), Substitute.For<IGoogleSyncWrite>()),
                SystemClock.Instance),
            BodyLinks(),
            authorization,
            Substitute.For<Microsoft.Extensions.Localization.IStringLocalizer<GovernanceResource>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = ct,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
                }
            },
        };
}
