using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using TeamService = Humans.Application.Services.Teams.TeamService;

namespace Humans.Application.Tests.Services.Teams;

/// <summary>
/// Logic of the Teams early-entry surface on <see cref="TeamService"/>:
/// the <c>IEarlyEntryProvider</c> feed, CRUD with team-enabled validation +
/// audit + cache eviction, the merge fold, the GDPR export slice, and
/// right-to-erasure. The repository is an NSubstitute mock — the EE remove /
/// reassign / delete-for-user repo methods use <c>ExecuteDelete/Update</c>,
/// which the InMemory provider the DB-backed harness uses cannot run, so these
/// tests assert the service's repo/invalidator/audit interactions directly.
/// </summary>
public sealed class TeamServiceEarlyEntryTests
{
    private readonly ITeamRepository _repo = Substitute.For<ITeamRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IEarlyEntryInvalidator _eeInvalidator = Substitute.For<IEarlyEntryInvalidator>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly TeamService _service;

    public TeamServiceEarlyEntryTests()
    {
        _service = new TeamService(
            _repo,
            _audit,
            Substitute.For<INotificationEmitter>(),
            Substitute.For<IShiftManagementService>(),
            Substitute.For<INotificationMeterCacheInvalidator>(),
            Substitute.For<IShiftAuthorizationInvalidator>(),
            Substitute.For<IAdminAuthorizationService>(),
            _eeInvalidator,
            new ServiceLocatorBuilder()
                .With<IGoogleSyncOutboxService>()
                .Build(),
            _clock,
            NullLogger<TeamService>.Instance);
    }

    private static TeamEarlyEntryGrant Grant(Guid teamId, Guid userId, string project = "P", LocalDate? date = null, string teamName = "Creativity") => new()
    {
        TeamId = teamId,
        Team = new Team { Id = teamId, Name = teamName },
        UserId = userId,
        ProjectName = project,
        EntryDate = date ?? new LocalDate(2026, 7, 5),
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        CreatedByUserId = Guid.NewGuid(),
    };

    // ==========================================================================
    // GetEarlyEntriesAsync (IEarlyEntryProvider feed)
    // ==========================================================================

    [HumansFact]
    public async Task GetEarlyEntriesAsync_ProjectsTeamNameLabel_FromEnabledTeams()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _repo.GetEarlyEntryGrantsForEnabledTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant>
            {
                Grant(teamId, userId, "Flaming Lotus", new LocalDate(2026, 7, 5), teamName: "Pyro")
            });

        var entries = await _service.GetEarlyEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        entries.Should().ContainSingle();
        entries[0].UserId.Should().Be(userId);
        entries[0].EntryDate.Should().Be(new LocalDate(2026, 7, 5));
        entries[0].Source.Should().Be("Pyro: Flaming Lotus");
    }

    [HumansFact]
    public async Task GetEarlyEntriesAsync_NoGrants_ReturnsEmpty()
    {
        _repo.GetEarlyEntryGrantsForEnabledTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant>());

        var entries = await _service.GetEarlyEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
    }

    // ==========================================================================
    // GetEarlyEntryGrantsForTeamAsync (management read model)
    // ==========================================================================

    [HumansFact]
    public async Task GetEarlyEntryGrantsForTeamAsync_ProjectsToReadModel()
    {
        var teamId = Guid.NewGuid();
        var grant = Grant(teamId, Guid.NewGuid(), "Lanterns", new LocalDate(2026, 7, 4));
        _repo.GetEarlyEntryGrantsForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant> { grant });

        var rows = await _service.GetEarlyEntryGrantsForTeamAsync(teamId, Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(grant.Id);
        row.UserId.Should().Be(grant.UserId);
        row.EntryDate.Should().Be(new LocalDate(2026, 7, 4));
        row.ProjectName.Should().Be("Lanterns");
    }

    // ==========================================================================
    // AddEarlyEntryGrantAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_TeamEnabled_AddsInvalidatesAudits()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var date = new LocalDate(2026, 7, 6);
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });

        await _service.AddEarlyEntryGrantAsync(teamId, userId, date, "  Trimmed Project  ", actor, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).AddEarlyEntryGrantAsync(
            Arg.Is<TeamEarlyEntryGrant>(g =>
                g.TeamId == teamId &&
                g.UserId == userId &&
                g.EntryDate == date &&
                g.ProjectName == "Trimmed Project" &&
                g.CreatedByUserId == actor &&
                g.CreatedAt == _clock.GetCurrentInstant()),
            Arg.Any<CancellationToken>());
        _eeInvalidator.Received(1).InvalidateUser(userId);
        await _audit.Received(1).LogAsync(
            AuditAction.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), Arg.Any<Guid>(),
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_TeamNotEnabled_ThrowsAndDoesNotAdd()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "No EE", Slug = "no-ee", EarlyEntryEnabled = false });

        var act = () => _service.AddEarlyEntryGrantAsync(
            teamId, Guid.NewGuid(), new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enabled*");
        await _repo.DidNotReceive().AddEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_EmptyUserId_Throws()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });

        var act = () => _service.AddEarlyEntryGrantAsync(
            teamId, Guid.Empty, new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        await _repo.DidNotReceive().AddEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_TeamMissing_Throws()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Team?)null);

        var act = () => _service.AddEarlyEntryGrantAsync(
            Guid.NewGuid(), Guid.NewGuid(), new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    // ==========================================================================
    // EditEarlyEntryGrantAsync
    // ==========================================================================

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_UpdatesFieldsInvalidatesAudits()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var grant = Grant(teamId, userId, "Old");
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        await _service.EditEarlyEntryGrantAsync(teamId, grant.Id, new LocalDate(2026, 7, 8), "  New Name ", actor, Xunit.TestContext.Current.CancellationToken);

        grant.EntryDate.Should().Be(new LocalDate(2026, 7, 8));
        grant.ProjectName.Should().Be("New Name");
        grant.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        await _repo.Received(1).UpdateEarlyEntryGrantAsync(grant, Arg.Any<CancellationToken>());
        _eeInvalidator.Received(1).InvalidateUser(userId);
        await _audit.Received(1).LogAsync(
            AuditAction.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_MissingGrant_Throws()
    {
        _repo.FindEarlyEntryGrantForMutationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamEarlyEntryGrant?)null);

        var act = () => _service.EditEarlyEntryGrantAsync(
            Guid.NewGuid(), Guid.NewGuid(), new LocalDate(2026, 7, 8), "x", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_GrantOnDifferentTeam_TreatedAsNotFound()
    {
        var grant = Grant(Guid.NewGuid(), Guid.NewGuid(), "Old");
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var act = () => _service.EditEarlyEntryGrantAsync(
            Guid.NewGuid(), grant.Id, new LocalDate(2026, 7, 8), "x", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
        await _repo.DidNotReceive().UpdateEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // RemoveEarlyEntryGrantAsync
    // ==========================================================================

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_DeletesInvalidatesAudits()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var grant = Grant(teamId, userId);
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        await _service.RemoveEarlyEntryGrantAsync(teamId, grant.Id, actor, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).RemoveEarlyEntryGrantAsync(grant.Id, Arg.Any<CancellationToken>());
        _eeInvalidator.Received(1).InvalidateUser(userId);
        await _audit.Received(1).LogAsync(
            AuditAction.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grant.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_MissingGrant_IsNoOp()
    {
        _repo.FindEarlyEntryGrantForMutationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamEarlyEntryGrant?)null);

        await _service.RemoveEarlyEntryGrantAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_GrantOnDifferentTeam_IsNoOp()
    {
        var grant = Grant(Guid.NewGuid(), Guid.NewGuid());
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        await _service.RemoveEarlyEntryGrantAsync(Guid.NewGuid(), grant.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // DeleteEarlyEntryGrantsForUserAsync (right-to-erasure)
    // ==========================================================================

    [HumansFact]
    public async Task DeleteEarlyEntryGrantsForUserAsync_HasGrants_RemovesAllInvalidates()
    {
        var userId = Guid.NewGuid();
        _repo.GetEarlyEntryGrantsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant> { Grant(Guid.NewGuid(), userId), Grant(Guid.NewGuid(), userId) });

        await _service.DeleteEarlyEntryGrantsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).RemoveEarlyEntryGrantsForUserAsync(userId, Arg.Any<CancellationToken>());
        _eeInvalidator.Received(1).InvalidateUser(userId);
    }

    [HumansFact]
    public async Task DeleteEarlyEntryGrantsForUserAsync_NoGrants_IsNoOp()
    {
        _repo.GetEarlyEntryGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant>());

        await _service.DeleteEarlyEntryGrantsForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _eeInvalidator.DidNotReceive().InvalidateUser(Arg.Any<Guid>());
    }

    // ==========================================================================
    // ReassignAsync (merge fold)
    // ==========================================================================

    [HumansFact]
    public async Task ReassignAsync_FoldsGrantsToTargetAndInvalidatesBoth()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        _repo.GetActiveByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());

        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).ReassignEarlyEntryGrantsAsync(source, target, Arg.Any<CancellationToken>());
        _eeInvalidator.Received(1).InvalidateUser(source);
        _eeInvalidator.Received(1).InvalidateUser(target);
    }

    // ==========================================================================
    // UpdateTeamAsync — EarlyEntryEnabled flag toggle
    // ==========================================================================

    [HumansFact]
    public async Task UpdateTeamAsync_EnablingEarlyEntry_SetsFlagAndInvalidatesAll()
    {
        var team = new Team { Id = Guid.NewGuid(), Name = "Toggle", Slug = "toggle", EarlyEntryEnabled = false };
        _repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.SlugExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await _service.UpdateTeamAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, isActive: true,
            earlyEntryEnabled: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        team.EarlyEntryEnabled.Should().BeTrue();
        _eeInvalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_FlagUnchanged_DoesNotInvalidate()
    {
        var team = new Team { Id = Guid.NewGuid(), Name = "Stable", Slug = "stable", EarlyEntryEnabled = false };
        _repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.SlugExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await _service.UpdateTeamAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, isActive: true,
            earlyEntryEnabled: false, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _eeInvalidator.DidNotReceive().InvalidateAll();
    }

    // ==========================================================================
    // ContributeForUserAsync (GDPR export)
    // ==========================================================================

    [HumansFact]
    public async Task ContributeForUserAsync_IncludesEarlyEntrySlice()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _repo.GetAllMembershipsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());
        _repo.GetAllJoinRequestsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamJoinRequest>());
        _repo.GetEarlyEntryGrantsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant> { Grant(teamId, userId, "Big Burn", new LocalDate(2026, 7, 9)) });
        _repo.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team> { [teamId] = new() { Id = teamId, Name = "Pyro", Slug = "pyro" } });

        var slices = await _service.ContributeForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var eeSlice = slices.Should().ContainSingle(s => s.SectionName == GdprExportSections.TeamEarlyEntry).Subject;
        var items = eeSlice.Data.Should().BeAssignableTo<System.Collections.IEnumerable>().Subject;
        items.Cast<object>().Should().ContainSingle();

        // Assert projected field values via JSON serialization (mirrors ExpenseReportServiceGdprTests pattern)
        var json = System.Text.Json.JsonSerializer.Serialize(eeSlice.Data);
        json.Should().Contain("\"TeamName\":\"Pyro\"");
        json.Should().Contain("\"ProjectName\":\"Big Burn\"");
        json.Should().Contain("\"EntryDate\":\"2026-07-09\"");
    }
}
