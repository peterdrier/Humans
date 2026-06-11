using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Verifies registered authorization policies through the real ASP.NET Core
/// authorization pipeline.
/// </summary>
public class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IAuthorizationService _authorizationService;
    private readonly IShiftManagementService _shiftManagement;

    public AuthorizationPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBudgetService>());
        services.AddScoped(_ => Substitute.For<ICampService>());
        services.AddScoped(_ => Substitute.For<ICampServiceRead>());
        services.AddScoped(_ => Substitute.For<ICityPlanningService>());
        services.AddScoped(_ => Substitute.For<ITeamService>());
        services.AddScoped(_ => Substitute.For<ITeamServiceRead>());
        services.AddScoped(_ => Substitute.For<IAgentRateLimitStore>());
        services.AddScoped(_ => Substitute.For<IAgentSettingsService>());
        // Expense resource-based handlers
        services.AddScoped(_ => Substitute.For<IExpenseReportServiceRead>());
        services.AddScoped(_ => Substitute.For<IExpenseReportService>());
        // IsAnyTeamManagerOrCoordinatorHandler reads team-coord ids through this service
        // (cached path); register a single shared substitute so per-test setups stick.
        _shiftManagement = Substitute.For<IShiftManagementService>();
        _shiftManagement.GetCoordinatorTeamIdsAsync(Arg.Any<Guid>()).Returns([]);
        services.AddSingleton(_shiftManagement);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddHumansAuthorizationPolicies();
        _serviceProvider = services.BuildServiceProvider();
        _authorizationService = _serviceProvider.GetRequiredService<IAuthorizationService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    public static TheoryData<string, string, bool> RolePolicyCases => new()
    {
        { PolicyNames.AdminOnly, RoleNames.Admin, true },
        { PolicyNames.AdminOnly, RoleNames.Board, false },
        { PolicyNames.AdminOnly, RoleNames.TeamsAdmin, false },
        { PolicyNames.AdminOnly, RoleNames.HumanAdmin, false },
        { PolicyNames.AdminOnly, RoleNames.FinanceAdmin, false },

        { PolicyNames.AnyAdminRole, RoleNames.Admin, true },
        { PolicyNames.AnyAdminRole, RoleNames.Board, true },
        { PolicyNames.AnyAdminRole, RoleNames.HumanAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.TeamsAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.CampAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.TicketAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.FeedbackAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.FinanceAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.NoInfoAdmin, true },
        { PolicyNames.AnyAdminRole, RoleNames.VolunteerCoordinator, true },
        { PolicyNames.AnyAdminRole, RoleNames.ConsentCoordinator, true },
        { PolicyNames.AnyAdminRole, "SomeNonAdminRole", false },

        { PolicyNames.BoardOnly, RoleNames.Board, true },
        { PolicyNames.BoardOnly, RoleNames.Admin, false },
        { PolicyNames.BoardOnly, RoleNames.HumanAdmin, false },

        { PolicyNames.BoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.BoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.BoardOrAdmin, RoleNames.TeamsAdmin, false },
        { PolicyNames.BoardOrAdmin, RoleNames.HumanAdmin, false },
        { PolicyNames.BoardOrAdmin, RoleNames.VolunteerCoordinator, false },

        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.HumanAdmin, true },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.TeamsAdmin, false },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.FinanceAdmin, false },

        { PolicyNames.HumanAdminOrAdmin, RoleNames.HumanAdmin, true },
        { PolicyNames.HumanAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.HumanAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.HumanAdminOrAdmin, RoleNames.TeamsAdmin, false },

        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.TeamsAdmin, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.HumanAdmin, false },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.CampAdmin, false },

        { PolicyNames.CampAdminOrAdmin, RoleNames.CampAdmin, true },
        { PolicyNames.CampAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.CampAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.CampAdminOrAdmin, RoleNames.TeamsAdmin, false },

        // Role short-circuits only; the team-coordinator path (no coordinated teams in
        // this fixture) is covered by the dedicated facts below.
        { PolicyNames.CampComplianceAccess, RoleNames.CampAdmin, true },
        { PolicyNames.CampComplianceAccess, RoleNames.Admin, true },
        { PolicyNames.CampComplianceAccess, RoleNames.Board, false },
        { PolicyNames.CampComplianceAccess, RoleNames.TeamsAdmin, false },
        { PolicyNames.CampComplianceAccess, RoleNames.VolunteerCoordinator, false },

        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.TicketAdmin, true },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.TeamsAdmin, false },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.HumanAdmin, false },

        { PolicyNames.TicketAdminOrAdmin, RoleNames.TicketAdmin, true },
        { PolicyNames.TicketAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TicketAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.TicketAdminOrAdmin, RoleNames.TeamsAdmin, false },

        { PolicyNames.ScannerAccess, RoleNames.TicketAdmin, true },
        { PolicyNames.ScannerAccess, RoleNames.Board, true },
        { PolicyNames.ScannerAccess, RoleNames.Admin, true },
        { PolicyNames.ScannerAccess, RoleNames.TeamsAdmin, false },
        { PolicyNames.ScannerAccess, RoleNames.HumanAdmin, false },
        { PolicyNames.ScannerAccess, "SomeNonAdminRole", false },

        { PolicyNames.FeedbackAdminOrAdmin, RoleNames.FeedbackAdmin, true },
        { PolicyNames.FeedbackAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.FeedbackAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.FeedbackAdminOrAdmin, RoleNames.TeamsAdmin, false },

        { PolicyNames.FinanceAdminOrAdmin, RoleNames.FinanceAdmin, true },
        { PolicyNames.FinanceAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.FinanceAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.FinanceAdminOrAdmin, RoleNames.TeamsAdmin, false },

        { PolicyNames.ReviewQueueAccess, RoleNames.ConsentCoordinator, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.VolunteerCoordinator, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.Board, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.Admin, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.TeamsAdmin, false },
        { PolicyNames.ReviewQueueAccess, RoleNames.HumanAdmin, false },

        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.ConsentCoordinator, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.VolunteerCoordinator, false },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.TeamsAdmin, false },

        { PolicyNames.ShiftDashboardAccess, RoleNames.Admin, true },
        { PolicyNames.ShiftDashboardAccess, RoleNames.NoInfoAdmin, true },
        { PolicyNames.ShiftDashboardAccess, RoleNames.VolunteerCoordinator, true },
        { PolicyNames.ShiftDashboardAccess, RoleNames.Board, false },
        { PolicyNames.ShiftDashboardAccess, RoleNames.TeamsAdmin, false },

        { PolicyNames.ShiftDepartmentManager, RoleNames.Admin, true },
        { PolicyNames.ShiftDepartmentManager, RoleNames.NoInfoAdmin, true },
        { PolicyNames.ShiftDepartmentManager, RoleNames.VolunteerCoordinator, true },
        { PolicyNames.ShiftDepartmentManager, RoleNames.Board, false },
        { PolicyNames.ShiftDepartmentManager, RoleNames.TeamsAdmin, false },

        { PolicyNames.PrivilegedSignupApprover, RoleNames.Admin, true },
        { PolicyNames.PrivilegedSignupApprover, RoleNames.NoInfoAdmin, true },
        { PolicyNames.PrivilegedSignupApprover, RoleNames.VolunteerCoordinator, false },
        { PolicyNames.PrivilegedSignupApprover, RoleNames.Board, false },

        { PolicyNames.VolunteerManager, RoleNames.Admin, true },
        { PolicyNames.VolunteerManager, RoleNames.VolunteerCoordinator, true },
        { PolicyNames.VolunteerManager, RoleNames.NoInfoAdmin, false },
        { PolicyNames.VolunteerManager, RoleNames.Board, false },

        { PolicyNames.MedicalDataViewer, RoleNames.Admin, true },
        { PolicyNames.MedicalDataViewer, RoleNames.NoInfoAdmin, true },
        { PolicyNames.MedicalDataViewer, RoleNames.Board, false },
        { PolicyNames.MedicalDataViewer, RoleNames.VolunteerCoordinator, false },

        // AppAccess = Active state only. Roles alone do not bypass onboarding/status routing.
        { PolicyNames.AppAccess, RoleNames.Admin, false },
        { PolicyNames.AppAccess, RoleNames.NoInfoAdmin, false },
        { PolicyNames.AppAccess, RoleNames.VolunteerCoordinator, false },
        { PolicyNames.AppAccess, RoleNames.CampAdmin, false },
        { PolicyNames.AppAccess, RoleNames.CantinaAdmin, false },
    };

    public static TheoryData<string> AnonymousPolicyCases =>
    [
        PolicyNames.AdminOnly,
        PolicyNames.AnyAdminRole,
        PolicyNames.BoardOnly,
        PolicyNames.ScannerAccess
    ];

    public static TheoryData<string[], bool> HumanAdminOnlyCases => new()
    {
        { [RoleNames.HumanAdmin], true },
        { [RoleNames.HumanAdmin, RoleNames.Admin], false },
        { [RoleNames.HumanAdmin, RoleNames.Board], false },
        { [RoleNames.Admin], false },
        { [], false },
    };

    [HumansTheory]
    [MemberData(nameof(RolePolicyCases))]
    public async Task Role_policy_matrix_matches_expected_roles(string policyName, string role, bool expected)
    {
        var result = await AuthorizeAsync(policyName, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [MemberData(nameof(AnonymousPolicyCases))]
    public async Task Policies_deny_unauthenticated_users(string policyName)
    {
        var result = await AuthorizeAnonymousAsync(policyName);
        result.Succeeded.Should().BeFalse();
    }

    // --- AnyAdminRole (admin-shell entry point) ---

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.HumanAdmin, true)]
    [InlineData(RoleNames.TeamsAdmin, true)]
    [InlineData(RoleNames.CampAdmin, true)]
    [InlineData(RoleNames.TicketAdmin, true)]
    [InlineData(RoleNames.EventsAdmin, true)]
    [InlineData(RoleNames.FeedbackAdmin, true)]
    [InlineData(RoleNames.FinanceAdmin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.ConsentCoordinator, true)]
    public async Task AnyAdminRole_AllowsAllAdminShapedRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.AnyAdminRole, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansFact]
    public async Task AnyAdminRole_DeniesUnauthenticated()
    {
        var result = await AuthorizeAnonymousAsync(PolicyNames.AnyAdminRole);
        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task AnyAdminRole_DeniesAuthenticatedNonAdmin()
    {
        // Authenticated user with no admin-shaped role (e.g. a regular member)
        // must not reach the admin shell.
        var result = await AuthorizeAsync(PolicyNames.AnyAdminRole, "SomeNonAdminRole");
        result.Succeeded.Should().BeFalse();
    }

    // --- BoardOnly ---

    [HumansTheory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task BoardOnly_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.BoardOnly, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansFact]
    public async Task BoardOnly_DeniesUnauthenticated()
    {
        var result = await AuthorizeAnonymousAsync(PolicyNames.BoardOnly);
        result.Succeeded.Should().BeFalse();
    }

    // --- BoardOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    public async Task BoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.BoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- HumanAdminBoardOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.HumanAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.FinanceAdmin, false)]
    public async Task HumanAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- HumanAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.HumanAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task HumanAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TeamsAdminBoardOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.TeamsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.HumanAdmin, false)]
    [InlineData(RoleNames.CampAdmin, false)]
    public async Task TeamsAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TeamsAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- CampAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.CampAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task CampAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.CampAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TicketAdminBoardOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.TicketAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task TicketAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TicketAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.TicketAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task TicketAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ScannerAccess (gate terminal) ---

    [HumansFact]
    public async Task ScannerAccess_AllowsGateTerminalAccountById()
    {
        // The gate account holds no roles; the policy admits it by well-known id.
        var user = CreateUserWithIdAndRoles(SystemUserIds.GateTerminal);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ScannerAccess);
        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task ScannerAccess_DeniesOrdinaryUserWithNoRoles()
    {
        var user = CreateUserWithIdAndRoles(Guid.NewGuid());
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ScannerAccess);
        result.Succeeded.Should().BeFalse();
    }

    // --- FeedbackAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.FeedbackAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task FeedbackAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.FeedbackAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- FinanceAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.FinanceAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task FinanceAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.FinanceAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- EventsAdminOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.EventsAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task EventsAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.EventsAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ReviewQueueAccess ---

    [HumansTheory]
    [InlineData(RoleNames.ConsentCoordinator, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task ReviewQueueAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ReviewQueueAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ConsentCoordinatorBoardOrAdmin ---

    [HumansTheory]
    [InlineData(RoleNames.ConsentCoordinator, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ConsentCoordinatorBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ConsentCoordinatorBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ShiftDashboardAccess ---

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ShiftDashboardAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ShiftDashboardAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ShiftDepartmentManager ---

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ShiftDepartmentManager_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ShiftDepartmentManager, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansFact]
    public async Task ShiftDepartmentManager_AllowsUserWithCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([Guid.NewGuid()]);

        var user = CreateUserWithIdAndRoles(userId, "SomeNonAdminRole");
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ShiftDepartmentManager);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task ShiftDepartmentManager_DeniesUserWithNoRoleAndNoCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([]);

        var user = CreateUserWithIdAndRoles(userId, "SomeNonAdminRole");
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ShiftDepartmentManager);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task ShiftDepartmentManager_PrivilegedRole_ShortCircuitsWithoutCallingShiftService()
    {
        _shiftManagement.ClearReceivedCalls();

        var result = await AuthorizeAsync(PolicyNames.ShiftDepartmentManager, RoleNames.Admin);

        result.Succeeded.Should().BeTrue();
        await _shiftManagement.DidNotReceive().GetCoordinatorTeamIdsAsync(Arg.Any<Guid>());
    }

    // --- CampComplianceAccess ---

    [HumansFact]
    public async Task CampComplianceAccess_AllowsUserWithCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([Guid.NewGuid()]);

        var user = CreateUserWithIdAndRoles(userId, "SomeNonAdminRole");
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.CampComplianceAccess);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task CampComplianceAccess_DeniesUserWithNoRoleAndNoCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([]);

        var user = CreateUserWithIdAndRoles(userId, "SomeNonAdminRole");
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.CampComplianceAccess);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task CampComplianceAccess_CampAdmin_ShortCircuitsWithoutCallingShiftService()
    {
        _shiftManagement.ClearReceivedCalls();

        var result = await AuthorizeAsync(PolicyNames.CampComplianceAccess, RoleNames.CampAdmin);

        result.Succeeded.Should().BeTrue();
        await _shiftManagement.DidNotReceive().GetCoordinatorTeamIdsAsync(Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task AppAccess_AllowsActiveStateUserWithNoRole()
    {
        var user = CreateUserWithClaim(
            RoleAssignmentClaimsTransformation.UserStateClaimType,
            UserState.Active.ToString());
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.AppAccess);
        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task AppAccess_DeniesRoleHolderWithoutActiveState()
    {
        // Roles alone do not bypass UserState.
        var user = CreateUserWithRoles(RoleNames.CantinaAdmin);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.AppAccess);
        result.Succeeded.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(nameof(UserState.Bare))]
    [InlineData(nameof(UserState.Suspended))]
    [InlineData(nameof(UserState.AdminSuspended))]
    [InlineData(nameof(UserState.Rejected))]
    public async Task AppAccess_DeniesNonActiveStateUserWithNoRole(string state)
    {
        var user = CreateUserWithClaim(
            RoleAssignmentClaimsTransformation.UserStateClaimType,
            state);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.AppAccess);
        result.Succeeded.Should().BeFalse();
    }

    [HumansTheory]
    [MemberData(nameof(HumanAdminOnlyCases))]
    public async Task HumanAdminOnly_matches_expected_role_combinations(string[] roles, bool expected)
    {
        var user = roles.Length == 0
            ? CreateAuthenticatedUser()
            : CreateUserWithRoles(roles);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.HumanAdminOnly);

        result.Succeeded.Should().Be(expected);
    }

    [HumansFact]
    public async Task UnknownPolicy_ThrowsInvalidOperationException()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var act = () => _authorizationService.AuthorizeAsync(user, "NonExistentPolicy");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private async Task<AuthorizationResult> AuthorizeAsync(string policyName, string role)
    {
        var user = CreateUserWithRoles(role);
        return await _authorizationService.AuthorizeAsync(user, policyName);
    }

    private async Task<AuthorizationResult> AuthorizeAnonymousAsync(string policyName)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        return await _authorizationService.AuthorizeAsync(user, policyName);
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles) =>
        CreateUserWithIdAndRoles(Guid.NewGuid(), roles);

    private static ClaimsPrincipal CreateUserWithIdAndRoles(Guid userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateAuthenticatedUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "testuser@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateUserWithClaim(string claimType, string claimValue)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "testuser@example.com"),
            new(claimType, claimValue)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}
