using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using AwesomeAssertions;
using Humans.Base.Caching;
using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Interfaces.Caching;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Teams.Data;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The repository is a substitute: its remove/reassign/delete-for-user methods use
/// <c>ExecuteDelete/Update</c>, which the InMemory provider cannot run.
/// </summary>
public sealed class TeamServiceEarlyEntryTests
{
    private readonly ITeamRepository _repo = Substitute.For<ITeamRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly TeamService _service;
    private readonly ISystemTeamSync _systemTeamSync = Substitute.For<ISystemTeamSync>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly Guid _eventId = Guid.NewGuid();

    public TeamServiceEarlyEntryTests()
    {
        var settings = new EventSettingsInfo(_eventId, "Current event", 2026, "Europe/Madrid",
            new LocalDate(2026, 7, 10), -10, 3, 5, new Dictionary<int, int>(), null, null);
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
        _settings.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == _eventId ? settings : null);
        _service = new TeamService(
            _repo,
            _audit,
            Substitute.For<INotificationEmitter>(),
            Substitute.For<IShiftManagement>(),
            Substitute.For<IAdminAuthorizationService>(),
            new ServiceLocatorBuilder()
                .With<IGoogleSyncOutboxService>()
                .With<IGoogleIntegrationService>()
                .With<ITeamResourceService>()
                .With(_users)
                .With(_systemTeamSync)
                .With(_roles)
                .With(_settings)
                .Build(),
            _clock,
            NullLogger<TeamService>.Instance);
    }

    [HumansTheory]
    [Xunit.InlineData((int)TeamUpdateConflict.SlugTaken, false)]
    [Xunit.InlineData((int)TeamUpdateConflict.CustomSlugTaken, false)]
    [Xunit.InlineData((int)TeamUpdateConflict.GroupPrefixTaken, false)]
    [Xunit.InlineData((int)TeamUpdateConflict.GroupPrefixTaken, true)]
    public async Task EditConflict_DoesNotProvisionGroupsOrAudit(int conflictValue, bool system)
    {
        var conflict = (TeamUpdateConflict)conflictValue;
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Alpha",
            Slug = "alpha",
            SystemTeamType = system ? SystemTeamType.Volunteers : SystemTeamType.None
        };
        _repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.UpdateTeamAsync(team, Arg.Any<CancellationToken>()).Returns(conflict);

        var result = await _service.UpdateTeamWithGoogleGroupAsync(
            team.Id, team.Name, null, false, true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Conflict.Should().Be(conflict);
        result.Team.Should().BeNull();
        _audit.ReceivedCalls().Should().BeEmpty();
        // The fixture has no Google-sync binding: reaching that call would return a warning/fault.
        result.GroupWarning.Should().BeNull();
    }

    [HumansFact]
    public async Task PageUpdateConflict_DoesNotAuditUnwrittenContent()
    {
        var team = new Team { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha" };
        _repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.UpdateTeamAsync(team, Arg.Any<CancellationToken>()).Returns(TeamUpdateConflict.SlugTaken);

        var result = await _service.UpdateTeamPageContentAsync(
            team.Id, "New content", [], false, false, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    private TeamEarlyEntryGrant Grant(Guid teamId, Guid userId, string project = "P", LocalDate? date = null, string teamName = "Creativity") => new()
    {
        TeamId = teamId,
        Team = new Team { Id = teamId, Name = teamName },
        UserId = userId,
        EventSettingsId = _eventId,
        ProjectName = project,
        EntryDate = date ?? new LocalDate(2026, 7, 5),
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        CreatedByUserId = Guid.NewGuid(),
    };

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

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GetEarlyEntriesAsync_ExcludesOtherEventsAndUnassignedGrants(bool noActiveEvent)
    {
        var current = Grant(Guid.NewGuid(), Guid.NewGuid());
        var other = Grant(Guid.NewGuid(), Guid.NewGuid());
        other.EventSettingsId = Guid.NewGuid(); // Same entry date/year, different event.
        var legacy = Grant(Guid.NewGuid(), Guid.NewGuid());
        legacy.EventSettingsId = null;
        _repo.GetEarlyEntryGrantsForEnabledTeamsAsync(Arg.Any<CancellationToken>()).Returns([current, other, legacy]);
        if (noActiveEvent)
            _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        var entries = await _service.GetEarlyEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        entries.Select(e => e.UserId).Should().Equal(noActiveEvent ? [] : [current.UserId]);
    }

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
        row.EventSettingsId.Should().Be(_eventId);
    }

    [HumansFact]
    public async Task GetEarlyEntriesAsync_TracksTheActiveEventInsteadOfCachingItsSelection()
    {
        var first = Grant(Guid.NewGuid(), Guid.NewGuid());
        var second = Grant(Guid.NewGuid(), Guid.NewGuid());
        second.EventSettingsId = Guid.NewGuid();
        _repo.GetEarlyEntryGrantsForEnabledTeamsAsync(Arg.Any<CancellationToken>()).Returns([first, second]);
        var active = await _settings.GetActiveEventSettingsAsync(Xunit.TestContext.Current.CancellationToken);
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(active, active! with { Id = second.EventSettingsId.Value });

        var firstRead = await _service.GetEarlyEntriesAsync(Xunit.TestContext.Current.CancellationToken);
        var secondRead = await _service.GetEarlyEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        firstRead.Select(e => e.UserId).Should().Equal(first.UserId);
        secondRead.Select(e => e.UserId).Should().Equal(second.UserId);
    }

    [HumansTheory]
    [Xunit.InlineData(false, "missing")]
    [Xunit.InlineData(true, "missing")]
    [Xunit.InlineData(false, "unknown")]
    [Xunit.InlineData(true, "unknown")]
    [Xunit.InlineData(false, "deleted")]
    [Xunit.InlineData(true, "deleted")]
    public async Task EarlyEntryMutations_RefuseUnavailableEventsBeforeWriting(bool edit, string kind)
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], null));
        var grant = Grant(teamId, userId);
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        var eventId = string.Equals(kind, "missing", StringComparison.Ordinal) ? Guid.Empty : Guid.NewGuid();
        if (string.Equals(kind, "deleted", StringComparison.Ordinal))
            _settings.GetEventSettingsByIdAsync(eventId, Arg.Any<CancellationToken>()).Returns(
                new EventSettingsInfo(eventId, "Deleted event", 2026, "Europe/Madrid", new LocalDate(2026, 7, 10),
                    -10, 3, 5, new Dictionary<int, int>(), null, null, EventSettingsStatus.Deleted));
        var originalDate = grant.EntryDate;
        var result = edit
            ? await _service.EditEarlyEntryGrantAsync(teamId, grant.Id, originalDate.PlusDays(1), "Changed", userId, eventId,
                Xunit.TestContext.Current.CancellationToken)
            : await _service.AddEarlyEntryGrantAsync(teamId, userId, originalDate, "New", userId, eventId,
                Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("event");
        grant.EventSettingsId.Should().Be(_eventId);
        grant.EntryDate.Should().Be(originalDate);
        _repo.ReceivedCalls().Should().NotContain(c =>
            string.Equals(c.GetMethodInfo().Name, nameof(ITeamRepository.AddEarlyEntryGrantAsync), StringComparison.Ordinal)
            || string.Equals(c.GetMethodInfo().Name, nameof(ITeamRepository.UpdateEarlyEntryGrantAsync), StringComparison.Ordinal));
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task AddEarlyEntryGrantAsync_TeamEnabled_AddsAndAuditsForResolvedPerson(bool mergedAlias, bool inactiveEvent)
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var resolvedId = mergedAlias ? Guid.NewGuid() : userId;
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = resolvedId }, [], [], null));
        var actor = Guid.NewGuid();
        var date = new LocalDate(2026, 7, 6);
        var eventId = _eventId;
        if (inactiveEvent)
        {
            eventId = Guid.NewGuid();
            var active = await _settings.GetActiveEventSettingsAsync(Xunit.TestContext.Current.CancellationToken);
            _settings.GetEventSettingsByIdAsync(eventId, Arg.Any<CancellationToken>())
                .Returns(active! with { Id = eventId, Status = EventSettingsStatus.Inactive });
        }
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });

        var result = await _service.AddEarlyEntryGrantAsync(teamId, userId, date, "  Trimmed Project  ", actor, eventId, Xunit.TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue();

        await _repo.Received(1).AddEarlyEntryGrantAsync(
            Arg.Is<TeamEarlyEntryGrant>(g =>
                g.TeamId == teamId &&
                g.UserId == resolvedId &&
                g.EventSettingsId == eventId &&
                g.EntryDate == date &&
                g.ProjectName == "Trimmed Project" &&
                g.CreatedByUserId == actor &&
                g.CreatedAt == _clock.GetCurrentInstant()),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            TeamsAuditActions.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), Arg.Any<Guid>(),
            Arg.Is<string>(description => description.Contains(resolvedId.ToString(), StringComparison.Ordinal)), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("deleted")]
    [Xunit.InlineData("merged")]
    [Xunit.InlineData("erased")]
    public async Task AddEarlyEntryGrantAsync_UnavailablePerson_DoesNotWriteOrAudit(string unavailable)
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, EarlyEntryEnabled = true });
        UserInfo? person = unavailable switch
        {
            "deleted" => UserInfo.Create(new User { Id = userId, State = UserState.Deleted }, [], [], null),
            "merged" => UserInfo.Create(new User { Id = userId, State = UserState.Merged }, [], [], null),
            "erased" => UserInfo.Create(new User { Id = userId }, [], [], null) with { IsGdprAnonymized = true },
            _ => null
        };
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(person);

        var result = await _service.AddEarlyEntryGrantAsync(teamId, userId, new LocalDate(2026, 7, 6),
            "Project", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("Cannot add a missing, deleted or merged account");
        await _repo.DidNotReceive().AddEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_TeamNotEnabled_ReturnsRefusalAndDoesNotAdd()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "No EE", Slug = "no-ee", EarlyEntryEnabled = false });

        var result = await _service.AddEarlyEntryGrantAsync(
            teamId, Guid.NewGuid(), new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("not enabled");
        await _repo.DidNotReceive().AddEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_EmptyUserId_ReturnsRefusal()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });

        var result = await _service.AddEarlyEntryGrantAsync(
            teamId, Guid.Empty, new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("non-empty GUID");
        await _repo.DidNotReceive().AddEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddEarlyEntryGrantAsync_TeamMissing_ReturnsRefusal()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Team?)null);

        var result = await _service.AddEarlyEntryGrantAsync(
            Guid.NewGuid(), Guid.NewGuid(), new LocalDate(2026, 7, 6), "x", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("not found");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task EarlyEntryMutations_TeamDisabledOrMissing_DoNotWriteOrAudit(bool remove, bool missingTeam)
    {
        var teamId = Guid.NewGuid();
        var grant = Grant(teamId, Guid.NewGuid(), "Original");
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);
        if (!missingTeam)
            _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
                .Returns(new Team { Id = teamId, Name = "Disabled", Slug = "disabled", EarlyEntryEnabled = false });
        var originalDate = grant.EntryDate;

        var result = remove
            ? await _service.RemoveEarlyEntryGrantAsync(teamId, grant.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken)
            : await _service.EditEarlyEntryGrantAsync(teamId, grant.Id, originalDate.PlusDays(1), "Changed",
                Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain(missingTeam ? "not found" : "not enabled");

        grant.EntryDate.Should().Be(originalDate);
        grant.ProjectName.Should().Be("Original");
        await _repo.DidNotReceive().UpdateEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().RemoveEarlyEntryGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_UpdatesFieldsAndAudits()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var grant = Grant(teamId, userId, "Old");
        grant.EventSettingsId = null;
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var result = await _service.EditEarlyEntryGrantAsync(teamId, grant.Id, new LocalDate(2026, 7, 8), "  New Name ", actor, _eventId, Xunit.TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue();

        grant.EntryDate.Should().Be(new LocalDate(2026, 7, 8));
        grant.ProjectName.Should().Be("New Name");
        grant.EventSettingsId.Should().Be(_eventId);
        grant.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        await _repo.Received(1).UpdateEarlyEntryGrantAsync(grant, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            TeamsAuditActions.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_MissingGrant_ReturnsRefusal()
    {
        _repo.FindEarlyEntryGrantForMutationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamEarlyEntryGrant?)null);

        var result = await _service.EditEarlyEntryGrantAsync(
            Guid.NewGuid(), Guid.NewGuid(), new LocalDate(2026, 7, 8), "x", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("not found");
    }

    [HumansFact]
    public async Task EditEarlyEntryGrantAsync_GrantOnDifferentTeam_TreatedAsNotFound()
    {
        var grant = Grant(Guid.NewGuid(), Guid.NewGuid(), "Old");
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        var result = await _service.EditEarlyEntryGrantAsync(
            Guid.NewGuid(), grant.Id, new LocalDate(2026, 7, 8), "x", Guid.NewGuid(), _eventId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("not found");
        await _repo.DidNotReceive().UpdateEarlyEntryGrantAsync(Arg.Any<TeamEarlyEntryGrant>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_DeletesAndAudits()
    {
        var teamId = Guid.NewGuid();
        _repo.GetByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Art", Slug = "art", EarlyEntryEnabled = true });
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var grant = Grant(teamId, userId);
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        await _service.RemoveEarlyEntryGrantAsync(teamId, grant.Id, actor, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).RemoveEarlyEntryGrantAsync(grant.Id, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            TeamsAuditActions.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grant.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_MissingGrant_IsNoOp()
    {
        _repo.FindEarlyEntryGrantForMutationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamEarlyEntryGrant?)null);

        await _service.RemoveEarlyEntryGrantAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveEarlyEntryGrantAsync_GrantOnDifferentTeam_IsNoOp()
    {
        var grant = Grant(Guid.NewGuid(), Guid.NewGuid());
        _repo.FindEarlyEntryGrantForMutationAsync(grant.Id, Arg.Any<CancellationToken>()).Returns(grant);

        await _service.RemoveEarlyEntryGrantAsync(Guid.NewGuid(), grant.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_EndsMembershipsDeletesRequestsAndGrants()
    {
        var userId = Guid.NewGuid();
        _repo.GetEarlyEntryGrantsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant> { Grant(Guid.NewGuid(), userId), Grant(Guid.NewGuid(), userId) });

        await _service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).RevokeAllMembershipsAsync(userId, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).DeleteJoinRequestsForUserAsync(userId, Arg.Any<CancellationToken>());
        await _repo.Received(1).RemoveEarlyEntryGrantsForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_NoGrants_SkipsGrantDelete()
    {
        _repo.GetEarlyEntryGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<TeamEarlyEntryGrant>());

        await _service.EraseForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RemoveEarlyEntryGrantsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReassignAsync_MovesRolesThroughAuthorizedRemoval_AndRetriesCoordinatorSync()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var team = new Team { Id = Guid.NewGuid(), Name = "Merge team", Slug = "merge-team" };
        var source = new TeamMember { Id = Guid.NewGuid(), TeamId = team.Id, Team = team, UserId = sourceId };
        var target = new TeamMember { Id = Guid.NewGuid(), TeamId = team.Id, Team = team, UserId = targetId };
        _repo.GetActiveByUserIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(_ => source.LeftAt is null ? new[] { source } : []);
        _repo.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team> { [team.Id] = team });
        _repo.GetByIdAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.FindActiveMemberForMutationAsync(team.Id, sourceId, Arg.Any<CancellationToken>()).Returns(source);
        _repo.FindActiveMemberForMutationAsync(team.Id, targetId, Arg.Any<CancellationToken>()).Returns(target);
        _roles.IsUserAdminAsync(actorId, Arg.Any<CancellationToken>()).Returns(true);
        _repo.MarkMemberLeftAsync(source.Id, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                source.LeftAt = call.Arg<Instant>();
                return Array.Empty<TeamRoleAssignment>();
            });
        _systemTeamSync.SyncMembershipForUserAsync(sourceId, SystemTeamType.Coordinators, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("sync unavailable")), Task.CompletedTask);
        var first = () => _service.ReassignAsync(sourceId, targetId, actorId,
            _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        await first.Should().ThrowAsync<InvalidOperationException>().WithMessage("sync unavailable");
        await _service.ReassignAsync(sourceId, targetId, actorId,
            _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkMemberLeftAsync(source.Id, Arg.Any<Instant>(), target.Id, Arg.Any<CancellationToken>());
        await _systemTeamSync.Received(2).SyncMembershipForUserAsync(
            sourceId, SystemTeamType.Coordinators, Arg.Any<CancellationToken>());
        await _systemTeamSync.Received(1).SyncMembershipForUserAsync(
            targetId, SystemTeamType.Coordinators, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReassignAsync_FoldsGrantsToTarget()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        _repo.GetActiveByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());

        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).ReassignEarlyEntryGrantsAsync(source, target, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateTeamAsync_EnablingEarlyEntry_SetsFlag()
    {
        var team = new Team { Id = Guid.NewGuid(), Name = "Toggle", Slug = "toggle", EarlyEntryEnabled = false };
        _repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        _repo.SlugExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await _service.UpdateTeamAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, isActive: true,
            earlyEntryEnabled: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        team.EarlyEntryEnabled.Should().BeTrue();
    }

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

        var eeSlice = slices.Should().ContainSingle(s => s.SectionName == TeamService.TeamEarlyEntry).Subject;
        var items = eeSlice.Data.Should().BeAssignableTo<System.Collections.IEnumerable>().Subject;
        items.Cast<object>().Should().ContainSingle();

        var json = System.Text.Json.JsonSerializer.Serialize(eeSlice.Data);
        json.Should().Contain("\"TeamName\":\"Pyro\"");
        json.Should().Contain($"\"EventSettingsId\":\"{_eventId}\"");
        json.Should().Contain("\"ProjectName\":\"Big Burn\"");
        json.Should().Contain("\"EntryDate\":\"2026-07-09\"");
    }
}
