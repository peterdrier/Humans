using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.GoogleIntegration.Contracts;
using Humans.Camps.Contracts;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Services;
using Humans.Teams.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// Regression tests for <see cref="SystemTeamSyncJob.SyncMembershipForUserAsync"/>.
/// Covers nobodies-collective/Humans#498: duplicate camp registration for a user who is already an active member of the
/// Barrio Leads system team must not violate IX_team_members_active_unique.
/// </summary>
/// <remarks>
/// The job owns no DbContext, so the test coordinates through the
/// <see cref="ITeamManagementService"/> / <see cref="ICampLeadDirectory"/> seams. The
/// Barrio Leads system team's <see cref="Team.Members"/> collection is stubbed
/// so the job's idempotency guard can see the existing active membership
/// without reading the DB directly.
/// </remarks>
public class SystemTeamSyncJobBarrioLeadsTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 15, 12, 0));
    private readonly ITeamManagementService _teamService = Substitute.For<ITeamManagementService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICampLeadDirectory _campRepository = Substitute.For<ICampLeadDirectory>();
    private readonly IGoogleSyncService _googleSyncService = Substitute.For<IGoogleSyncService>();
    private readonly IGoogleGroupSync _googleGroupSync = Substitute.For<IGoogleGroupSync>();
    private readonly IGoogleDriveActivityClient _googleClient = Substitute.For<IGoogleDriveActivityClient>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly TeamsEmails _emailMessages = TestTeamsEmails.Create();
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    private SystemTeamSyncJob CreateJob(IMembershipCalculatorRead? membershipCalculator = null, ITeamResourceService? resources = null)
    {
        _googleClient.IsConfigured.Returns(true);
        var services = new ServiceCollection();
        services.AddSingleton(membershipCalculator ?? Substitute.For<IMembershipCalculatorRead>());
        services.AddSingleton(resources ?? Substitute.For<ITeamResourceService>());
        var provider = services.BuildServiceProvider();

        return new SystemTeamSyncJob(
            _teamService,
            _userService,
            _campRepository,
            provider,
            _googleSyncService,
            _googleGroupSync,
            _googleClient,
            _auditLogService,
            _emailService,
            _emailMessages,
            _roleAssignmentClaimsInvalidator,
            _metrics,
            NullLogger<SystemTeamSyncJob>.Instance,
            _clock);
    }

    [HumansTheory]
    [Xunit.InlineData("es", "es")]
    [Xunit.InlineData(null, "en")]
    [Xunit.InlineData("", "en")]
    [Xunit.InlineData("pt", "en")]
    [Xunit.InlineData("fr-FR", "en")]
    [Xunit.InlineData("not a culture!", "en")]
    public async Task SyncVolunteersTeam_emails_added_members_in_supported_language(string? language, string expectedLanguage)
    {
        using var culture = new CultureScope("fr");
        var member = new User { Id = Guid.NewGuid(), BurnerName = "Member", PreferredLanguage = language! };
        var profile = new ProfileInfo(
            Id: Guid.NewGuid(),
            BurnerName: "Member",
            FirstName: "First",
            LastName: "Last",
            City: null,
            CountryCode: null,
            Latitude: null,
            Longitude: null,
            PlaceId: null,
            Bio: null,
            Pronouns: null,
            BirthdayDay: null,
            BirthdayMonth: null,
            EmergencyContactName: null,
            EmergencyContactPhone: null,
            EmergencyContactRelationship: null,
            DietaryPreference: null,
            Allergies: [],
            AllergyOtherText: null,
            Intolerances: [],
            IntoleranceOtherText: null,
            MedicalConditions: null,
            HasCustomPicture: false,
            ProfilePictureContentType: null,
            CreatedAt: _clock.GetCurrentInstant(),
            UpdatedAt: _clock.GetCurrentInstant(),
            AdminNotes: null,
            ContributionInterests: null,
            BoardNotes: null,
            Iban: null,
            IsApproved: false,
            MembershipTier: MembershipTier.Volunteer,
            ConsentCheckStatus: null,
            ConsentCheckAt: null,
            ConsentCheckedByUserId: null,
            ConsentCheckNotes: null,
            RejectionReason: null,
            RejectedAt: null,
            RejectedByUserId: null,
            NoPriorBurnExperience: false,
            ContactFields: [],
            Languages: [],
            VolunteerHistory: []);
        var user = UserInfo.Create(member,
            [new UserEmail { Id = Guid.NewGuid(), UserId = member.Id, Email = "member@example.com", IsVerified = true, IsPrimary = true }],
            [], [], profile, []);
        var team = new TeamInfo(SystemTeamIds.Volunteers, "Volunteers", null, "volunteers",
            true, true, SystemTeamType.Volunteers, false, false, false, false, default, []);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new[] { user });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo> { [user.Id] = user }));
        var calculator = Substitute.For<IMembershipCalculatorRead>();
        calculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(user.Id)), team.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { user.Id });
        var resources = Substitute.For<ITeamResourceService>();
        resources.GetTeamResourcesAsync(team.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<GoogleResourceSnapshot>());

        await CreateJob(calculator, resources).SyncVolunteersTeamAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await _teamService.Received(1).ApplySystemTeamMembershipDeltaAsync(team.Id,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(user.Id)),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(team.Id, user.Id,
            Arg.Any<CancellationToken>(), GoogleSyncSource.SystemTeamSync);
        var message = _emailService.ReceivedCalls().Should().ContainSingle().Which.GetArguments()[0]
            .Should().BeOfType<EmailMessage>().Subject;
        message.RecipientEmail.Should().Be("member@example.com");
        message.Subject.Should().EndWith($"#{expectedLanguage}");
        System.Globalization.CultureInfo.CurrentUICulture.Name.Should().Be("fr");
    }

    private SystemTeamMembershipSnapshot StubBarrioLeadsTeam(IEnumerable<TeamMember>? activeMembers = null)
    {
        var teamId = Guid.NewGuid();
        var team = new SystemTeamMembershipSnapshot(
            teamId,
            "Barrio Leads",
            "barrio-leads",
            IsHidden: true,
            SystemTeamType.BarrioLeads,
            activeMembers?
                .Where(member => member.LeftAt is null)
                .Select(member => member.UserId)
                .ToList() ?? []);
        var teamInfo = new TeamInfo(
            teamId, "Barrio Leads", null, "barrio-leads",
            IsActive: true, IsSystemTeam: true, SystemTeamType: SystemTeamType.BarrioLeads,
            RequiresApproval: false, IsPublicPage: false, IsHidden: true,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: team.ActiveMemberUserIds
                .Select(uid => new TeamMemberInfo(
                    Guid.NewGuid(), uid, string.Empty, null, null,
                    TeamMemberRole.Member, Instant.MinValue))
                .ToList());
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [teamId] = teamInfo });
        return team;
    }

    [HumansFact]
    public async Task SyncMembershipForUserAsync_BarrioLeads_UserAlreadyActiveMember_IsNoOp()
    {
        // User is already an active member of Barrio Leads and is still a
        // lead of at least one camp — the guard in the job should short-circuit.
        var userId = Guid.NewGuid();
        StubBarrioLeadsTeam(
            [
                new TeamMember
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Role = TeamMemberRole.Member,
                    JoinedAt = _clock.GetCurrentInstant(),
                    LeftAt = null,
                },
            ]);
        _campRepository.IsLeadAnywhereAsync(userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var job = CreateJob();

        // Act + Assert: should not throw and must not call the membership
        // delta apply path (otherwise a duplicate insert would surface).
        var act = async () => await job.SyncMembershipForUserAsync(userId, SystemTeamType.BarrioLeads, Xunit.TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();

        await _teamService.DidNotReceive().ApplySystemTeamMembershipDeltaAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncMembershipForUserAsync_BarrioLeads_UserBecomesLead_AddsMembership()
    {
        // User is a new lead but not yet a team member — the job should
        // enqueue the add via the bulk-delta service call.
        var userId = Guid.NewGuid();
        var team = StubBarrioLeadsTeam();
        _campRepository.IsLeadAnywhereAsync(userId, Arg.Any<CancellationToken>())
            .Returns(true);

        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var job = CreateJob();

        await job.SyncMembershipForUserAsync(userId, SystemTeamType.BarrioLeads, Xunit.TestContext.Current.CancellationToken);

        await _teamService.Received(1).ApplySystemTeamMembershipDeltaAsync(
            team.Id,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            team.Id,
            userId,
            Arg.Any<CancellationToken>(),
            GoogleSyncSource.SystemTeamSync);
    }

    [HumansFact]
    public async Task SyncMembershipForUserAsync_InvalidatesTeamCacheBeforeReading()
    {
        // Several services call the per-user sync from inside their own mutation,
        // before CachingTeamService has invalidated — the job must drop the cache
        // first or its eligibility reads see the pre-mutation team set.
        var userId = Guid.NewGuid();
        StubBarrioLeadsTeam();
        _campRepository.IsLeadAnywhereAsync(userId, Arg.Any<CancellationToken>())
            .Returns(false);

        var job = CreateJob();

        await job.SyncMembershipForUserAsync(userId, SystemTeamType.BarrioLeads, Xunit.TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _teamService.InvalidateActiveTeamsCache();
            _ = _teamService.GetTeamsAsync(Arg.Any<CancellationToken>());
        });
    }

    [HumansFact]
    public async Task ExecuteAsync_WithoutCredentials_SkipsGoogleGroupReconcile()
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        var job = CreateJob();
        _googleClient.IsConfigured.Returns(false);

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleGroupSync.DidNotReceiveWithAnyArgs().ReconcileAllAsync(default, default);
        _metrics.Received(1).RecordJobRun("system_team_sync", "success");
    }

    [HumansFact]
    public async Task ExecuteAsync_WithCredentials_ReconcilesGoogleGroups()
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        var job = CreateJob();

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleGroupSync.Received(1).ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>());
    }
}
