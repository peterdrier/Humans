using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Regression tests for <see cref="SystemTeamSyncJob.SyncBarrioLeadsMembershipForUserAsync"/>.
/// Covers #498: duplicate camp registration for a user who is already an active member of the
/// Barrio Leads system team must not violate IX_team_members_active_unique.
/// </summary>
/// <remarks>
/// Rewritten for the §15 Google-writing jobs migration (issue #570): the job no
/// longer owns a <c>HumansDbContext</c>, so the test coordinates through the
/// <see cref="ITeamService"/> / <see cref="ICampRepository"/> seams. The
/// Barrio Leads system team's <see cref="Team.Members"/> collection is stubbed
/// so the job's idempotency guard can see the existing active membership
/// without reading the DB directly.
/// </remarks>
public class SystemTeamSyncJobBarrioLeadsTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 15, 12, 0));
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICampRepository _campRepository = Substitute.For<ICampRepository>();
    private readonly IGoogleSyncService _googleSyncService = Substitute.For<IGoogleSyncService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    private SystemTeamSyncJob CreateJob()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMembershipCalculator>());
        var provider = services.BuildServiceProvider();

        return new SystemTeamSyncJob(
            _teamService,
            _userService,
            _campRepository,
            provider,
            _googleSyncService,
            _auditLogService,
            _emailService,
            _roleAssignmentClaimsInvalidator,
            _metrics,
            NullLogger<SystemTeamSyncJob>.Instance,
            _clock);
    }

    private Team StubBarrioLeadsTeam(IEnumerable<TeamMember>? activeMembers = null)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Barrio Leads",
            Slug = "barrio-leads",
            Description = "System team for barrio leads",
            SystemTeamType = SystemTeamType.BarrioLeads,
            IsActive = true,
            IsHidden = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        if (activeMembers is not null)
        {
            foreach (var m in activeMembers)
            {
                team.Members.Add(m);
            }
        }
        _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.BarrioLeads, Arg.Any<CancellationToken>())
            .Returns(team);
        return team;
    }

    [HumansFact]
    public async Task SyncBarrioLeadsMembershipForUserAsync_UserAlreadyActiveMember_IsNoOp()
    {
        // User is already an active member of Barrio Leads and is still a
        // lead of at least one camp — the guard in the job should short-circuit.
        var userId = Guid.NewGuid();
        var team = StubBarrioLeadsTeam(
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
        var act = async () => await job.SyncBarrioLeadsMembershipForUserAsync(userId);
        await act.Should().NotThrowAsync();

        await _teamService.DidNotReceive().ApplySystemTeamMembershipDeltaAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncBarrioLeadsMembershipForUserAsync_UserBecomesLead_AddsMembership()
    {
        // User is a new lead but not yet a team member — the job should
        // enqueue the add via the bulk-delta service call.
        var userId = Guid.NewGuid();
        var team = StubBarrioLeadsTeam();
        _campRepository.IsLeadAnywhereAsync(userId, Arg.Any<CancellationToken>())
            .Returns(true);

        _userService.GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, User>)new Dictionary<Guid, User>());

        var job = CreateJob();

        await job.SyncBarrioLeadsMembershipForUserAsync(userId);

        await _teamService.Received(1).ApplySystemTeamMembershipDeltaAsync(
            team.Id,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
    }
}
