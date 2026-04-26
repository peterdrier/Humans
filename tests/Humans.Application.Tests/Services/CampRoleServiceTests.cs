using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampRoleServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampRoleService _service;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly ICampService _campService;
    private readonly Guid _actorUserId = Guid.NewGuid();

    public CampRoleServiceTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(_options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 26, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();
        _userService = Substitute.For<IUserService>();
        _notificationEmitter = Substitute.For<INotificationEmitter>();
        _campService = Substitute.For<ICampService>();

        var factory = new TestDbContextFactory(_options);
        var repo = new CampRoleRepository(factory);

        _service = new CampRoleService(
            repo,
            _campService,
            _userService,
            _auditLog,
            _notificationEmitter,
            _clock,
            NullLogger<CampRoleService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ListDefinitions_excludes_deactivated_by_default()
    {
        await SeedDefinitionAsync("Active Role");
        await SeedDefinitionAsync("Old Role", deactivated: true);

        var result = await _service.ListDefinitionsAsync(includeDeactivated: false);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Active Role");
    }

    [HumansFact]
    public async Task ListDefinitions_includes_deactivated_when_requested()
    {
        await SeedDefinitionAsync("Active Role");
        await SeedDefinitionAsync("Old Role", deactivated: true);

        var result = await _service.ListDefinitionsAsync(includeDeactivated: true);

        result.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetDefinitionById_returns_definition_when_found()
    {
        var def = await SeedDefinitionAsync("Build Lead");

        var result = await _service.GetDefinitionByIdAsync(def.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Build Lead");
    }

    [HumansFact]
    public async Task GetDefinitionById_returns_null_when_not_found()
    {
        var result = await _service.GetDefinitionByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task CreateDefinition_persists_and_audits()
    {
        var input = new CreateCampRoleDefinitionInput(
            Name: "Sound Lead", Description: "Manages sound system",
            SlotCount: 1, MinimumRequired: 1, SortOrder: 60, IsRequired: false);

        var result = await _service.CreateDefinitionAsync(input, _actorUserId);

        result.Name.Should().Be("Sound Lead");
        result.SlotCount.Should().Be(1);
        result.IsRequired.Should().BeFalse();

        // Audit happens AFTER save (I1 fix)
        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionCreated,
            nameof(CampRoleDefinition),
            result.Id,
            Arg.Any<string>(),
            _actorUserId,
            null, null);
    }

    [HumansFact]
    public async Task CreateDefinition_rejects_duplicate_name()
    {
        await SeedDefinitionAsync("Consent Lead");

        var input = new CreateCampRoleDefinitionInput(
            Name: "Consent Lead", Description: null,
            SlotCount: 1, MinimumRequired: 1, SortOrder: 99, IsRequired: false);

        var act = async () => await _service.CreateDefinitionAsync(input, _actorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [HumansFact]
    public async Task CreateDefinition_rejects_minimumRequired_above_slotCount()
    {
        var input = new CreateCampRoleDefinitionInput(
            Name: "Bad Role", Description: null,
            SlotCount: 1, MinimumRequired: 2, SortOrder: 99, IsRequired: true);

        var act = async () => await _service.CreateDefinitionAsync(input, _actorUserId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*MinimumRequired*");
    }

    [HumansFact]
    public async Task UpdateDefinition_modifies_fields_and_audits()
    {
        var def = await SeedDefinitionAsync("Old Name", slotCount: 1);

        var input = new UpdateCampRoleDefinitionInput(
            Name: "New Name", Description: "Updated description",
            SlotCount: 2, MinimumRequired: 1, SortOrder: 99, IsRequired: false);

        var ok = await _service.UpdateDefinitionAsync(def.Id, input, _actorUserId);

        ok.Should().BeTrue();
        var updated = await _service.GetDefinitionByIdAsync(def.Id);
        updated!.Name.Should().Be("New Name");
        updated.SlotCount.Should().Be(2);
        updated.IsRequired.Should().BeFalse();

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            def.Id,
            Arg.Any<string>(),
            _actorUserId,
            null, null);
    }

    [HumansFact]
    public async Task UpdateDefinition_rejects_duplicate_name()
    {
        var def1 = await SeedDefinitionAsync("Consent Lead");
        var def2 = await SeedDefinitionAsync("LNT");

        var input = new UpdateCampRoleDefinitionInput(
            Name: "Consent Lead", Description: null,
            SlotCount: def2.SlotCount, MinimumRequired: def2.MinimumRequired,
            SortOrder: def2.SortOrder, IsRequired: def2.IsRequired);

        var act = async () => await _service.UpdateDefinitionAsync(def2.Id, input, _actorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task UpdateDefinition_returns_false_when_not_found()
    {
        var input = new UpdateCampRoleDefinitionInput(
            Name: "Anything", Description: null,
            SlotCount: 1, MinimumRequired: 0, SortOrder: 0, IsRequired: false);

        var ok = await _service.UpdateDefinitionAsync(Guid.NewGuid(), input, _actorUserId);

        ok.Should().BeFalse();
    }

    [HumansFact]
    public async Task DeactivateDefinition_sets_DeactivatedAt_and_audits()
    {
        var def = await SeedDefinitionAsync("Will Be Deactivated");

        var ok = await _service.DeactivateDefinitionAsync(def.Id, _actorUserId);

        ok.Should().BeTrue();
        var reloaded = await _service.GetDefinitionByIdAsync(def.Id);
        reloaded!.DeactivatedAt.Should().NotBeNull();

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionDeactivated,
            nameof(CampRoleDefinition), def.Id, Arg.Any<string>(), _actorUserId, null, null);
    }

    [HumansFact]
    public async Task DeactivateDefinition_returns_false_when_not_found()
    {
        var ok = await _service.DeactivateDefinitionAsync(Guid.NewGuid(), _actorUserId);
        ok.Should().BeFalse();
    }

    [HumansFact]
    public async Task ReactivateDefinition_clears_DeactivatedAt_and_audits()
    {
        var def = await SeedDefinitionAsync("Was Deactivated", deactivated: true);

        var ok = await _service.ReactivateDefinitionAsync(def.Id, _actorUserId);

        ok.Should().BeTrue();
        var reloaded = await _service.GetDefinitionByIdAsync(def.Id);
        reloaded!.DeactivatedAt.Should().BeNull();

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition), def.Id, Arg.Any<string>(), _actorUserId, null, null);
    }

    [HumansFact]
    public async Task Assign_happy_path_creates_assignment_and_audits_and_notifies()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var member = await SeedActiveMemberAsync(season.Id);
        var def = await SeedDefinitionAsync();

        _campService.GetCampMemberStatusAsync(member.Id, default)
            .Returns(new CampMemberLookup(season.Id, member.UserId, CampMemberStatus.Active));

        var outcome = await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId);

        outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var assignments = await _dbContext.CampRoleAssignments.AsNoTracking().ToListAsync();
        assignments.Should().HaveCount(1);
        assignments[0].CampMemberId.Should().Be(member.Id);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleAssigned, nameof(CampRoleAssignment),
            Arg.Any<Guid>(), Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());

        await _notificationEmitter.Received(1).SendAsync(
            NotificationSource.CampRoleAssigned,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(r => r.Contains(member.UserId)),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Assign_returns_MemberNotActive_when_member_is_pending()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var member = await SeedActiveMemberAsync(season.Id);
        var def = await SeedDefinitionAsync();

        _campService.GetCampMemberStatusAsync(member.Id, default)
            .Returns(new CampMemberLookup(season.Id, member.UserId, CampMemberStatus.Pending));

        var outcome = await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId);

        outcome.Should().Be(AssignCampRoleOutcome.MemberNotActive);
        (await _dbContext.CampRoleAssignments.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task Assign_returns_MemberSeasonMismatch_when_member_is_in_different_season()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var (_, otherSeason) = await SeedCampWithSeasonAsync();
        var member = await SeedActiveMemberAsync(otherSeason.Id);
        var def = await SeedDefinitionAsync();

        _campService.GetCampMemberStatusAsync(member.Id, default)
            .Returns(new CampMemberLookup(otherSeason.Id, member.UserId, CampMemberStatus.Active));

        var outcome = await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId);

        outcome.Should().Be(AssignCampRoleOutcome.MemberSeasonMismatch);
    }

    [HumansFact]
    public async Task Assign_returns_SlotCapReached_when_full()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var def = await SeedDefinitionAsync(slotCount: 1);
        var member1 = await SeedActiveMemberAsync(season.Id);
        var member2 = await SeedActiveMemberAsync(season.Id);

        _campService.GetCampMemberStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                if (id == member1.Id) return new CampMemberLookup(season.Id, member1.UserId, CampMemberStatus.Active);
                if (id == member2.Id) return new CampMemberLookup(season.Id, member2.UserId, CampMemberStatus.Active);
                return null;
            });

        (await _service.AssignAsync(season.Id, def.Id, member1.Id, _actorUserId))
            .Should().Be(AssignCampRoleOutcome.Assigned);

        (await _service.AssignAsync(season.Id, def.Id, member2.Id, _actorUserId))
            .Should().Be(AssignCampRoleOutcome.SlotCapReached);
    }

    [HumansFact]
    public async Task Assign_returns_AlreadyHoldsRole_on_duplicate_member()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var def = await SeedDefinitionAsync(slotCount: 2);
        var member = await SeedActiveMemberAsync(season.Id);

        _campService.GetCampMemberStatusAsync(member.Id, default)
            .Returns(new CampMemberLookup(season.Id, member.UserId, CampMemberStatus.Active));

        (await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId))
            .Should().Be(AssignCampRoleOutcome.Assigned);
        (await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId))
            .Should().Be(AssignCampRoleOutcome.AlreadyHoldsRole);
    }

    [HumansFact]
    public async Task Assign_returns_RoleDeactivated_when_definition_is_soft_deleted()
    {
        var (camp, season) = await SeedCampWithSeasonAsync();
        var def = await SeedDefinitionAsync(deactivated: true);
        var member = await SeedActiveMemberAsync(season.Id);

        _campService.GetCampMemberStatusAsync(member.Id, default)
            .Returns(new CampMemberLookup(season.Id, member.UserId, CampMemberStatus.Active));

        var outcome = await _service.AssignAsync(season.Id, def.Id, member.Id, _actorUserId);

        outcome.Should().Be(AssignCampRoleOutcome.RoleDeactivated);
    }

    private async Task<CampRoleDefinition> SeedDefinitionAsync(
        string name = "Consent Lead", int slotCount = 2, int minimumRequired = 1,
        bool isRequired = true, bool deactivated = false)
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlotCount = slotCount,
            MinimumRequired = minimumRequired,
            IsRequired = isRequired,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
            DeactivatedAt = deactivated ? _clock.GetCurrentInstant() : null,
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync();
        return def;
    }

    private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(int year = 2026)
    {
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = $"camp-{Guid.NewGuid():N}".Substring(0, 12),
        };
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = year,
            Name = "Test Camp",
            Status = CampSeasonStatus.Active,
        };
        _dbContext.Camps.Add(camp);
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync();
        return (camp, season);
    }

    private async Task<CampMember> SeedActiveMemberAsync(Guid seasonId, Guid? userId = null)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            UserId = userId ?? Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
            ConfirmedByUserId = _actorUserId,
        };
        _dbContext.CampMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        return member;
    }
}
