using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Permissions.Services;
using NSubstitute;
using Xunit;

namespace Humans.Permissions.Tests;

public sealed class PermissionServiceTests
{
    private const string Team = "Team";

    private static readonly Permission ShiftsManage = new(
        "Shifts.Manage", "Manage shifts", "", [Team], TopLevelOnly: false, AdminRoles: ["ShiftsAdmin"], typeof(PermissionServiceTests));

    private static readonly Permission TeamsManage = new(
        "Teams.Manage", "Manage the team", "", [Team], TopLevelOnly: false, AdminRoles: ["TeamsAdmin"], typeof(PermissionServiceTests));

    private static readonly Permission BudgetEdit = new(
        "Budget.Edit", "Edit the budget", "", [Team], TopLevelOnly: true, AdminRoles: ["FinanceAdmin"], typeof(PermissionServiceTests));

    // Production (department) > Cantina, Power (projects). Art is another department.
    private static readonly Guid Production = Guid.NewGuid();
    private static readonly Guid Cantina = Guid.NewGuid();
    private static readonly Guid Power = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();
    private static readonly Guid Closed = Guid.NewGuid();
    private static readonly Guid SystemTeam = Guid.NewGuid();

    private static readonly OwnerNode[] Tree =
    [
        new(Production, null, IsActive: true, IsSystem: false),
        new(Cantina, Production, IsActive: true, IsSystem: false),
        new(Power, Production, IsActive: true, IsSystem: false),
        new(Art, null, IsActive: true, IsSystem: false),
        new(Closed, null, IsActive: false, IsSystem: false),
        new(SystemTeam, null, IsActive: true, IsSystem: true),
    ];

    private readonly Guid _userId = Guid.NewGuid();

    public static TheoryData<string, string, string, bool> Cases => new()
    {
        // holding, permission, target, expected
        { "coordinator:Production", "Shifts.Manage", "Production", true },
        { "coordinator:Production", "Shifts.Manage", "Cantina", true },
        { "coordinator:Production", "Shifts.Manage", "Art", false },
        { "coordinator:Cantina", "Shifts.Manage", "Cantina", true },
        { "coordinator:Cantina", "Shifts.Manage", "Production", false },
        { "coordinator:Cantina", "Shifts.Manage", "Power", false },
        { "Shifts.Manage:Cantina", "Shifts.Manage", "Cantina", true },
        { "Shifts.Manage:Cantina", "Teams.Manage", "Cantina", false },
        { "coordinator:Production", "Budget.Edit", "Cantina", true },
        { "coordinator:Cantina", "Budget.Edit", "Cantina", false },
        { "coordinator:Closed", "Shifts.Manage", "Closed", false },
        { "coordinator:SystemTeam", "Shifts.Manage", "SystemTeam", false },
        { "none", "Shifts.Manage", "Cantina", false },
        { "role:ShiftsAdmin", "Shifts.Manage", "Art", true },
        { "role:ShiftsAdmin", "Teams.Manage", "Art", false },
        { "role:TeamsAdmin", "Budget.Edit", "Production", false },
        { "role:Admin", "Budget.Edit", "Cantina", true },
    };

    [HumansTheory]
    [MemberData(nameof(Cases))]
    public async Task HasAsync_FollowsTheRules(string holding, string key, string target, bool expected)
    {
        var service = Build(holding);

        var result = await service.HasAsync(_userId, key, new Owner(Team, Id(target)), TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task GetOwnersAsync_ReturnsTheOwnerAndEverythingBelowIt()
    {
        var service = Build("coordinator:Production");

        var owners = await service.GetOwnersAsync(_userId, "Shifts.Manage", TestContext.Current.CancellationToken);

        owners.Select(o => o.Id).Should().BeEquivalentTo([Production, Cantina, Power]);
    }

    [HumansFact]
    public async Task HasAsync_RefusesAnUndeclaredKey()
    {
        var service = Build("none");

        var act = () => service.HasAsync(_userId, "Nope.Manage", new Owner(Team, Cantina), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [HumansFact]
    public void Permissions_RefusesAKeyDeclaredTwice()
    {
        var service = new PermissionService(
            [new Declares(ShiftsManage), new Declares(ShiftsManage)], [], [], Substitute.For<IRoleAssignmentService>());

        var act = () => service.Permissions;

        act.Should().Throw<InvalidOperationException>();
    }

    private PermissionService Build(string holding)
    {
        var roles = Substitute.For<IRoleAssignmentService>();
        var holdings = new List<Holding>();
        var parts = holding.Split(':');

        if (string.Equals(parts[0], "role", StringComparison.Ordinal))
            roles.GetActiveForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns([new RoleAssignmentSnapshot(parts[1], null)]);
        else
            roles.GetActiveForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);

        if (string.Equals(parts[0], "coordinator", StringComparison.Ordinal))
            holdings.Add(new Holding(new Owner(Team, Id(parts[1])), IsCoordinator: true, [], "coordinator"));
        else if (parts[0].Contains('.', StringComparison.Ordinal))
            holdings.Add(new Holding(new Owner(Team, Id(parts[1])), IsCoordinator: false, [parts[0]], "narrow role"));

        return new PermissionService(
            [new Declares(ShiftsManage, TeamsManage, BudgetEdit)],
            [new Teams()],
            [new Source(holdings)],
            roles);
    }

    private static Guid Id(string name) => name switch
    {
        "Production" => Production,
        "Cantina" => Cantina,
        "Power" => Power,
        "Art" => Art,
        "Closed" => Closed,
        "SystemTeam" => SystemTeam,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private sealed class Declares(params Permission[] permissions) : ISectionPermissions
    {
        public IReadOnlyList<Permission> Permissions => permissions;
    }

    private sealed class Teams : IOwnerKind
    {
        public string Kind => Team;

        public Task<IReadOnlyList<OwnerNode>> GetOwnersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OwnerNode>>(Tree);
    }

    private sealed class Source(IReadOnlyList<Holding> holdings) : IPermissionSource
    {
        public Task<IReadOnlyList<Holding>> GetHoldingsAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(holdings);
    }
}
