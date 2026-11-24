using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Roles.Data;
using Humans.Roles.Domain;
using Humans.Roles.Services;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Roles.Tests;

public sealed class RoleServiceTests
{
    private const string Team = "Team";

    // Production (department) > Cantina, Power (projects). Art is another department.
    private static readonly Guid Production = Guid.NewGuid();
    private static readonly Guid Cantina = Guid.NewGuid();
    private static readonly Guid Power = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();
    private static readonly Guid Old = Guid.NewGuid();

    private static readonly OwnerNode[] Tree =
    [
        new(Production, null, IsActive: true, IsSystem: false),
        new(Cantina, Production, IsActive: true, IsSystem: false),
        new(Power, Production, IsActive: true, IsSystem: false),
        new(Art, null, IsActive: true, IsSystem: false),
        new(Old, null, IsActive: false, IsSystem: false),
    ];

    private static readonly Permission ShiftsManage = new(
        "Shifts.Manage", "Manage shifts", "", [Team], TopLevelOnly: false, AdminRoles: [], typeof(RolesResource));

    private static readonly Permission BudgetEdit = new(
        "Budget.Edit", "Edit the budget", "", [Team], TopLevelOnly: true, AdminRoles: [], typeof(RolesResource));

    private readonly Guid _actor = Guid.NewGuid();
    private readonly FakeRepository _repository = new();
    private readonly FakeOwner _owner = new();
    private readonly IRoleAssignmentService _systemRoles = Substitute.For<IRoleAssignmentService>();
    private readonly RoleService _service;

    public RoleServiceTests()
    {
        _systemRoles.GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _service = new RoleService(
            _repository,
            [new Teams()],
            [_owner],
            [new Declares(ShiftsManage, BudgetEdit)],
            [],
            _systemRoles,
            Substitute.For<IAuditLogService>(),
            new FakeClock(Instant.FromUtc(2026, 11, 21, 12, 0)),
            new KeyLocalizer(),
            new KeyLocalizerFactory());
    }

    public static TheoryData<string, string, bool> ManageCases => new()
    {
        // who the actor is, owner, expected
        { "coordinator:Production", "Production", true },
        { "coordinator:Production", "Cantina", true },
        { "coordinator:Cantina", "Cantina", true },
        { "coordinator:Cantina", "Production", false },
        { "coordinator:Cantina", "Power", false },
        { "narrow:Cantina", "Cantina", false },
        { "none", "Cantina", false },
        { "system:RolesAdmin", "Art", true },
        { "system:Admin", "Art", true },
        { "system:TeamsAdmin", "Art", false },
    };

    [HumansTheory]
    [MemberData(nameof(ManageCases))]
    public async Task CanManageAsync_CoordinatorsManageTheirOwnerAndBelow(string actor, string owner, bool expected)
    {
        Arrange(actor);

        var result = await _service.CanManageAsync(new Owner(Team, Id(owner)), _actor, TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task CreateAsync_OnlyAdminsCreateTheCoordinatorRole()
    {
        Arrange("coordinator:Production");

        var result = await _service.CreateAsync(Owner(Cantina), Input("Cantina coordinator", coordinator: true), _actor, Ct);

        result.Succeeded.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("link", false, "Roles_Refusal_LinkTooLong")]
    [InlineData("description", false, "Roles_Refusal_DescriptionTooLong")]
    [InlineData("priorities", false, "Roles_Refusal_PrioritiesTooLong")]
    [InlineData("link", true, "Roles_Refusal_LinkTooLong")]
    [InlineData("description", true, "Roles_Refusal_DescriptionTooLong")]
    [InlineData("priorities", true, "Roles_Refusal_PrioritiesTooLong")]
    public async Task SaveAsync_RefusesValuesThatExceedStoredLimits(string field, bool update, string refusal)
    {
        Arrange("system:RolesAdmin");
        var input = field switch
        {
            "link" => Input("Planner") with { CustomSlug = new string('a', 101) },
            "description" => Input("Planner") with { Description = new string('a', 2001) },
            _ => Input("Planner") with { SlotCount = 51, Priorities = [.. Enumerable.Repeat(SlotPriority.Important, 51)] },
        };
        var existing = Role(Cantina, "Existing");
        if (update) _repository.Seed(existing);

        var result = update
            ? await _service.UpdateAsync(existing.Id, input, _actor, Ct)
            : await _service.CreateAsync(Owner(Cantina), input, _actor, Ct);

        result.Refusal.Should().Be(refusal);
        if (update)
            existing.Name.Should().Be("Existing");
        else
            _repository.Roles.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("link")]
    [InlineData("description")]
    [InlineData("priorities")]
    [InlineData("unicode-name")]
    public async Task CreateAsync_AcceptsValuesAtStoredLimits(string field)
    {
        Arrange("system:RolesAdmin");
        var input = field switch
        {
            "link" => Input("Planner") with { CustomSlug = new string('a', 100) },
            "description" => Input("Planner") with { Description = string.Concat(Enumerable.Repeat("😀", 2000)) },
            "priorities" => Input("Planner") with { SlotCount = 50, Priorities = [.. Enumerable.Repeat(SlotPriority.Important, 50)] },
            _ => Input("a" + string.Concat(Enumerable.Repeat("😀", 99))),
        };

        var result = await _service.CreateAsync(Owner(Cantina), input, _actor, Ct);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task CreateAsync_MakesASlugAndKeepsPermissionKeys()
    {
        Arrange("coordinator:Production");

        var result = await _service.CreateAsync(Owner(Cantina), Input("Shift planner", keys: ["Shifts.Manage"]), _actor, Ct);

        result.Role!.Slug.Should().Be("shift-planner");
        result.Role.PermissionKeys.Should().Equal("Shifts.Manage");
    }

    public static TheoryData<string, string[]> RefusedInputs => new()
    {
        { "shift planner", [] },
        { "Lead", ["Budget.Edit"] },
        { "Lead", ["Nope.Manage"] },
    };

    [HumansTheory]
    [MemberData(nameof(RefusedInputs))]
    public async Task CreateAsync_RefusesBadInput(string name, string[] keys)
    {
        Arrange("coordinator:Production");
        _repository.Seed(Role(Cantina, "Shift planner"));

        var result = await _service.CreateAsync(Owner(Cantina), Input(name, keys: keys), _actor, Ct);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task CreateAsync_RefusesASecondCoordinatorRole()
    {
        Arrange("system:RolesAdmin");
        _repository.Seed(Role(Cantina, "Coordinator", coordinator: true));

        var result = await _service.CreateAsync(Owner(Cantina), Input("Lead", coordinator: true), _actor, Ct);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task AssignAsync_AsksTheOwnerFirstAndTakesTheLowestOpenSlot()
    {
        Arrange("coordinator:Production");
        var role = Role(Cantina, "Shift planner", slots: 3);
        role.Assignments.Add(new RoleAssignment { Id = Guid.NewGuid(), RoleId = role.Id, UserId = Guid.NewGuid(), Slot = 0 });
        _repository.Seed(role);
        var person = Guid.NewGuid();

        var result = await _service.AssignAsync(role.Id, person, _actor, Ct);

        _owner.Asked.Should().Equal((Cantina, person));
        result.Role!.Holders.Should().Contain(h => h.UserId == person && h.Slot == 1);
    }

    [HumansFact]
    public async Task AssignAsync_StopsWhenTheOwnerRefuses()
    {
        Arrange("coordinator:Production");
        var role = Role(Cantina, "Shift planner");
        _repository.Seed(role);
        _owner.Refusal = "System teams don't have roles.";

        var result = await _service.AssignAsync(role.Id, Guid.NewGuid(), _actor, Ct);

        result.Refusal.Should().Be("System teams don't have roles.");
        _repository.Roles.Single(r => r.Id == role.Id).Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task RemoveAsync_TakesThePersonOutOfThatOwnersRolesOnly()
    {
        var person = Guid.NewGuid();
        var cantinaRole = Role(Cantina, "Shift planner");
        var artRole = Role(Art, "Painter");
        foreach (var role in new[] { cantinaRole, artRole })
            role.Assignments.Add(new RoleAssignment { Id = Guid.NewGuid(), RoleId = role.Id, UserId = person, Slot = 0 });
        _repository.Seed(cantinaRole);
        _repository.Seed(artRole);

        await _service.RemoveAsync(Owner(Cantina), person, person, Ct);

        cantinaRole.Assignments.Should().BeEmpty();
        artRole.Assignments.Should().ContainSingle();
    }

    public static TheoryData<string, string, bool, bool> VisibleCases => new()
    {
        // who the actor is, owner, public, expected
        { "none", "Cantina", true, true },
        { "none", "Cantina", false, false },
        { "coordinator:Production", "Cantina", false, true },
        { "system:Board", "Cantina", false, true },
        { "none", "Art", true, false },
        { "system:Board", "Art", true, true },
        { "system:Board", "Old", true, false },
    };

    [HumansTheory]
    [MemberData(nameof(VisibleCases))]
    public async Task GetVisibleRolesAsync_HidesPrivateHiddenAndInactive(string actor, string owner, bool isPublic, bool expected)
    {
        Arrange(actor);
        var role = Role(Id(owner), "Shift planner");
        role.IsPublic = isPublic;
        _repository.Seed(role);

        var visible = await _service.GetVisibleRolesAsync(_actor, Ct);

        visible.Any(r => r.Id == role.Id).Should().Be(expected);
    }

    [HumansFact]
    public async Task GetContactsAsync_FallsBackToTheNearestCoordinatorAbove()
    {
        var lead = Guid.NewGuid();
        var coordinator = Role(Production, "Coordinator", coordinator: true);
        coordinator.Assignments.Add(new RoleAssignment { Id = Guid.NewGuid(), RoleId = coordinator.Id, UserId = lead, Slot = 0 });
        _repository.Seed(coordinator);
        _repository.Seed(Role(Cantina, "Coordinator", coordinator: true));

        var contacts = await _service.GetContactsAsync(Owner(Cantina), Ct);

        contacts.Should().Equal(lead);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Arrange(string actor)
    {
        var parts = actor.Split(':');
        switch (parts[0])
        {
            case "system":
                _systemRoles.GetActiveForUserAsync(_actor, Arg.Any<CancellationToken>())
                    .Returns([new RoleAssignmentSnapshot(parts[1], null)]);
                break;
            case "coordinator":
            case "narrow":
                var role = Role(Id(parts[1]), parts[0], coordinator: string.Equals(parts[0], "coordinator", StringComparison.Ordinal));
                role.Assignments.Add(new RoleAssignment { Id = Guid.NewGuid(), RoleId = role.Id, UserId = _actor, Slot = 0 });
                _repository.Seed(role);
                break;
        }
    }

    private static Owner Owner(Guid id) => new(Team, id);

    private static Role Role(Guid owner, string name, bool coordinator = false, int slots = 1) => new()
    {
        Id = Guid.NewGuid(),
        OwnerKind = Team,
        OwnerId = owner,
        Name = name,
        Slug = name.ToLowerInvariant().Replace(' ', '-'),
        SlotCount = slots,
        Priorities = [.. Enumerable.Repeat(SlotPriority.Important, slots)],
        IsCoordinator = coordinator,
    };

    private static RoleInput Input(string name, bool coordinator = false, IReadOnlyList<string>? keys = null) => new(
        name, null, null, 1, [SlotPriority.Important], 0, RolePeriod.YearRound, null, true, coordinator, keys ?? []);

    private static Guid Id(string name) => name switch
    {
        "Production" => Production,
        "Cantina" => Cantina,
        "Power" => Power,
        "Art" => Art,
        "Old" => Old,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private sealed class Teams : IOwnerKind
    {
        public string Kind => Team;

        public Task<IReadOnlyList<OwnerNode>> GetOwnersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OwnerNode>>(Tree);
    }

    private sealed class FakeOwner : IRoleOwner
    {
        public List<(Guid, Guid)> Asked { get; } = [];

        public string? Refusal { get; set; }

        public string Kind => Team;

        public Task<string?> EnsureMemberAsync(Guid ownerId, Guid userId, Guid actorUserId, CancellationToken ct = default)
        {
            Asked.Add((ownerId, userId));
            return Task.FromResult(Refusal);
        }

        /// <summary>Art is hidden.</summary>
        public Task<IReadOnlyList<RoleOwnerInfo>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RoleOwnerInfo>>(
                [.. Tree.Select(n => new RoleOwnerInfo(n.Id, n.Id.ToString("N"), n.Id.ToString("N"), IsHidden: n.Id == Art, "/"))]);
    }

    private sealed class KeyLocalizer : IStringLocalizer<RolesResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class KeyLocalizerFactory : IStringLocalizerFactory
    {
        public IStringLocalizer Create(Type resourceSource) => new KeyLocalizer();

        public IStringLocalizer Create(string baseName, string location) => new KeyLocalizer();
    }

    private sealed class Declares(params Permission[] permissions) : ISectionPermissions
    {
        public IReadOnlyList<Permission> Permissions => permissions;
    }

    private sealed class FakeRepository : IRoleRepository
    {
        public List<Role> Roles { get; } = [];

        public void Seed(Role role) => Roles.Add(role);

        public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Role>>(Roles);

        public Task AddAsync(Role role, CancellationToken ct = default)
        {
            Roles.Add(role);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Role role, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default) =>
            Task.FromResult(Roles.RemoveAll(r => r.Id == roleId) > 0);

        public Task<bool> AddAssignmentAsync(RoleAssignment assignment, CancellationToken ct = default)
        {
            Roles.Single(r => r.Id == assignment.RoleId).Assignments.Add(assignment);
            return Task.FromResult(true);
        }

        public Task<bool> RemoveAssignmentAsync(Guid roleId, Guid userId, CancellationToken ct = default)
        {
            var role = Roles.Single(r => r.Id == roleId);
            var row = role.Assignments.FirstOrDefault(a => a.UserId == userId);
            return Task.FromResult(row is not null && role.Assignments.Remove(row));
        }

        public Task<IReadOnlyList<Guid>> RemoveAssignmentsAsync(
            Guid userId, IReadOnlyCollection<Guid>? roleIds, CancellationToken ct = default)
        {
            var removed = new List<Guid>();
            foreach (var role in Roles.Where(r => roleIds is null || roleIds.Contains(r.Id)))
            {
                if (role.Assignments.FirstOrDefault(a => a.UserId == userId) is { } row && role.Assignments.Remove(row))
                    removed.Add(role.Id);
            }

            return Task.FromResult<IReadOnlyList<Guid>>(removed);
        }

        public Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
