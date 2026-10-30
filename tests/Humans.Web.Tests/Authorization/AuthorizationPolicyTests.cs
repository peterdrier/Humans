using Humans.Tickets.Contracts;
using Humans.Finance.Contracts;
using Humans.Consent.Contracts;
using Humans.Events.Contracts;
using Humans.Feedback.Contracts;
using Humans.Cantina.Contracts;
using Humans.Rideshare.Contracts;
using System.Reflection;
using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Shifts.Authorization;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Humans.Base.Authorization;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Authorization;
using Humans.Users.Contracts;

namespace Humans.Web.Tests.Authorization;

/// <summary>Runs the registered policies through the real ASP.NET Core authorization pipeline.</summary>
public class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IAuthorizationService _authorizationService;
    private readonly IShiftService _shiftManagement;

    public AuthorizationPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBudgetService>());
        services.AddScoped(_ => Substitute.For<ICampService>());
        services.AddScoped(_ => Substitute.For<ICityPlanningService>());
        services.AddScoped(_ => Substitute.For<ITeamService>());
        services.AddScoped(_ => Substitute.For<ITeamService>());
        // One shared substitute, so per-test setups reach IsAnyTeamManagerOrCoordinatorHandler.
        _shiftManagement = Substitute.For<IShiftService>();
        _shiftManagement.GetCoordinatorTeamIdsAsync(Arg.Any<Guid>()).Returns([]);
        services.AddSingleton(_shiftManagement);
        services.AddSingleton<IClock>(SystemClock.Instance);
        // Section-contributed policies: their handlers come from each Section.Register, so register them here.
        services.AddScoped<IAuthorizationHandler, IsAnyTeamManagerOrCoordinatorHandler>();
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();
        // AnyAdminRole derives from the sections' ISectionRoles through Auth's RoleCatalog.
        foreach (var roles in SectionDiscoveryExtensions.DiscoverImplementations<ISectionRoles>())
            services.AddSingleton(roles);
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));
        services.AddHumansAuthorizationPolicies();
        _serviceProvider = services.BuildServiceProvider();
        _authorizationService = _serviceProvider.GetRequiredService<IAuthorizationService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    [HumansFact]
    public void Fallback_policy_requires_an_authenticated_user()
    {
        var options = _serviceProvider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.Should().NotBeNull("an endpoint with no authorization metadata must not be public");
        options.FallbackPolicy!.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<DenyAnonymousAuthorizationRequirement>();
    }

    public static TheoryData<string, string, bool> RolePolicyCases => new()
    {
        { PolicyNames.AdminOnly, RoleNames.Admin, true },
        { PolicyNames.AdminOnly, RoleNames.Board, false },
        { PolicyNames.AdminOnly, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.AdminOnly, UsersRoles.UsersAdmin, false },
        { PolicyNames.AdminOnly, FinanceRoles.FinanceAdmin, false },

        { PolicyNames.AnyAdminRole, RoleNames.Admin, true },
        { PolicyNames.AnyAdminRole, RoleNames.Board, true },
        { PolicyNames.AnyAdminRole, UsersRoles.UsersAdmin, true },
        { PolicyNames.AnyAdminRole, TeamsRoles.TeamsAdmin, true },
        { PolicyNames.AnyAdminRole, CampsRoles.CampsAdmin, true },
        { PolicyNames.AnyAdminRole, TicketsRoles.TicketsAdmin, true },
        { PolicyNames.AnyAdminRole, FeedbackRoles.FeedbackAdmin, true },
        { PolicyNames.AnyAdminRole, RideshareRoles.RideshareAdmin, true },
        { PolicyNames.AnyAdminRole, FinanceRoles.FinanceAdmin, true },
        { PolicyNames.AnyAdminRole, ShiftsRoles.ShiftsNoInfo, true },
        { PolicyNames.AnyAdminRole, ShiftsRoles.ShiftsAdmin, true },
        { PolicyNames.AnyAdminRole, ConsentRoles.ConsentCoordinator, true },
        { PolicyNames.AnyAdminRole, "SomeNonAdminRole", false },

        { PolicyNames.BoardOnly, RoleNames.Board, true },
        { PolicyNames.BoardOnly, RoleNames.Admin, false },
        { PolicyNames.BoardOnly, UsersRoles.UsersAdmin, false },

        { PolicyNames.BoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.BoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.BoardOrAdmin, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.BoardOrAdmin, UsersRoles.UsersAdmin, false },
        { PolicyNames.BoardOrAdmin, ShiftsRoles.ShiftsAdmin, false },

        { PolicyNames.HumanAdminBoardOrAdmin, UsersRoles.UsersAdmin, true },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.HumanAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.HumanAdminBoardOrAdmin, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.HumanAdminBoardOrAdmin, FinanceRoles.FinanceAdmin, false },

        { PolicyNames.HumanAdminOrAdmin, UsersRoles.UsersAdmin, true },
        { PolicyNames.HumanAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.HumanAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.HumanAdminOrAdmin, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.TeamsAdminBoardOrAdmin, TeamsRoles.TeamsAdmin, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TeamsAdminBoardOrAdmin, UsersRoles.UsersAdmin, false },
        { PolicyNames.TeamsAdminBoardOrAdmin, CampsRoles.CampsAdmin, false },

        { PolicyNames.CampAdminOrAdmin, CampsRoles.CampsAdmin, true },
        { PolicyNames.CampAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.CampAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.CampAdminOrAdmin, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.TicketAdminBoardOrAdmin, TicketsRoles.TicketsAdmin, true },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TicketAdminBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.TicketAdminBoardOrAdmin, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.TicketAdminBoardOrAdmin, UsersRoles.UsersAdmin, false },

        { PolicyNames.TicketAdminOrAdmin, TicketsRoles.TicketsAdmin, true },
        { PolicyNames.TicketAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.TicketAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.TicketAdminOrAdmin, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.ScannerAccess, TicketsRoles.TicketsAdmin, true },
        { PolicyNames.ScannerAccess, RoleNames.Board, true },
        { PolicyNames.ScannerAccess, RoleNames.Admin, true },
        { PolicyNames.ScannerAccess, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.ScannerAccess, UsersRoles.UsersAdmin, false },
        { PolicyNames.ScannerAccess, "SomeNonAdminRole", false },

        { PolicyNames.GateAdmit, TicketsRoles.TicketsAdmin, true },
        { PolicyNames.GateAdmit, RoleNames.Board, true },
        { PolicyNames.GateAdmit, RoleNames.Admin, true },
        { PolicyNames.GateAdmit, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.GateAdmit, UsersRoles.UsersAdmin, false },
        { PolicyNames.GateAdmit, "SomeNonAdminRole", false },

        { PolicyNames.FinanceAdminOrAdmin, FinanceRoles.FinanceAdmin, true },
        { PolicyNames.FinanceAdminOrAdmin, RoleNames.Admin, true },
        { PolicyNames.FinanceAdminOrAdmin, RoleNames.Board, false },
        { PolicyNames.FinanceAdminOrAdmin, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.ReviewQueueAccess, ConsentRoles.ConsentCoordinator, true },
        { PolicyNames.ReviewQueueAccess, ShiftsRoles.ShiftsAdmin, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.Board, true },
        { PolicyNames.ReviewQueueAccess, RoleNames.Admin, true },
        { PolicyNames.ReviewQueueAccess, TeamsRoles.TeamsAdmin, false },
        { PolicyNames.ReviewQueueAccess, UsersRoles.UsersAdmin, false },

        { PolicyNames.ConsentCoordinatorBoardOrAdmin, ConsentRoles.ConsentCoordinator, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.Board, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, RoleNames.Admin, true },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, ShiftsRoles.ShiftsAdmin, false },
        { PolicyNames.ConsentCoordinatorBoardOrAdmin, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.ShiftDashboardAccess, RoleNames.Admin, true },
        { PolicyNames.ShiftDashboardAccess, ShiftsRoles.ShiftsNoInfo, true },
        { PolicyNames.ShiftDashboardAccess, ShiftsRoles.ShiftsAdmin, true },
        { PolicyNames.ShiftDashboardAccess, RoleNames.Board, false },
        { PolicyNames.ShiftDashboardAccess, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.ShiftDepartmentManager, RoleNames.Admin, true },
        { PolicyNames.ShiftDepartmentManager, ShiftsRoles.ShiftsNoInfo, true },
        { PolicyNames.ShiftDepartmentManager, ShiftsRoles.ShiftsAdmin, true },
        { PolicyNames.ShiftDepartmentManager, RoleNames.Board, false },
        { PolicyNames.ShiftDepartmentManager, TeamsRoles.TeamsAdmin, false },

        { PolicyNames.PrivilegedSignupApprover, RoleNames.Admin, true },
        { PolicyNames.PrivilegedSignupApprover, ShiftsRoles.ShiftsNoInfo, true },
        { PolicyNames.PrivilegedSignupApprover, ShiftsRoles.ShiftsAdmin, false },
        { PolicyNames.PrivilegedSignupApprover, RoleNames.Board, false },

        { PolicyNames.VolunteerManager, RoleNames.Admin, true },
        { PolicyNames.VolunteerManager, ShiftsRoles.ShiftsAdmin, true },
        { PolicyNames.VolunteerManager, ShiftsRoles.ShiftsNoInfo, false },
        { PolicyNames.VolunteerManager, RoleNames.Board, false },

        { PolicyNames.MedicalDataViewer, RoleNames.Admin, true },
        { PolicyNames.MedicalDataViewer, ShiftsRoles.ShiftsNoInfo, true },
        { PolicyNames.MedicalDataViewer, RoleNames.Board, true },
        { PolicyNames.MedicalDataViewer, ShiftsRoles.ShiftsAdmin, false },

        // AppAccess = Active state only. Roles alone do not bypass onboarding/status routing.
        { PolicyNames.AppAccess, RoleNames.Admin, false },
        { PolicyNames.AppAccess, ShiftsRoles.ShiftsNoInfo, false },
        { PolicyNames.AppAccess, ShiftsRoles.ShiftsAdmin, false },
        { PolicyNames.AppAccess, CampsRoles.CampsAdmin, false },
        { PolicyNames.AppAccess, CantinaRoles.CantinaAdmin, false },
    };

    public static TheoryData<string> AnonymousPolicyCases =>
    [
        PolicyNames.AdminOnly,
        PolicyNames.AnyAdminRole,
        PolicyNames.BoardOnly,
        PolicyNames.ScannerAccess,
        PolicyNames.GateAdmit
    ];

    public static TheoryData<string[], bool> HumanAdminOnlyCases => new()
    {
        { [UsersRoles.UsersAdmin], true },
        { [UsersRoles.UsersAdmin, RoleNames.Admin], false },
        { [UsersRoles.UsersAdmin, RoleNames.Board], false },
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

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(UsersRoles.UsersAdmin, true)]
    [InlineData(TeamsRoles.TeamsAdmin, true)]
    [InlineData(CampsRoles.CampsAdmin, true)]
    [InlineData(TicketsRoles.TicketsAdmin, true)]
    [InlineData(EventsRoles.EventsAdmin, true)]
    [InlineData(FeedbackRoles.FeedbackAdmin, true)]
    [InlineData(FinanceRoles.FinanceAdmin, true)]
    [InlineData(ShiftsRoles.ShiftsNoInfo, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, true)]
    [InlineData(ConsentRoles.ConsentCoordinator, true)]
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
        var result = await AuthorizeAsync(PolicyNames.AnyAdminRole, "SomeNonAdminRole");
        result.Succeeded.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, false)]
    [InlineData(UsersRoles.UsersAdmin, false)]
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

    [HumansTheory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    [InlineData(UsersRoles.UsersAdmin, false)]
    [InlineData(ShiftsRoles.ShiftsAdmin, false)]
    public async Task BoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.BoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(UsersRoles.UsersAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    [InlineData(FinanceRoles.FinanceAdmin, false)]
    public async Task HumanAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(UsersRoles.UsersAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task HumanAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(TeamsRoles.TeamsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(UsersRoles.UsersAdmin, false)]
    [InlineData(CampsRoles.CampsAdmin, false)]
    public async Task TeamsAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TeamsAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(CampsRoles.CampsAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task CampAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.CampAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(TicketsRoles.TicketsAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    [InlineData(UsersRoles.UsersAdmin, false)]
    public async Task TicketAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(TicketsRoles.TicketsAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task TicketAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

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

    [HumansFact]
    public async Task GateAdmit_AllowsGateTerminalAccountById()
    {
        // The shared gate kiosk account holds no roles; the write policy admits it by well-known id.
        var user = CreateUserWithIdAndRoles(SystemUserIds.GateTerminal);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.GateAdmit);
        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task GateAdmit_DeniesOrdinaryUserWithNoRoles()
    {
        var user = CreateUserWithIdAndRoles(Guid.NewGuid());
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.GateAdmit);
        result.Succeeded.Should().BeFalse();
    }

    // Feedback is retired and Admin-only. These read the policy off FeedbackController itself, so loosening the
    // attribute fails here.

    [HumansFact]
    public async Task FeedbackController_DeniesAuthenticatedUserWithNoRoles()
    {
        var result = await _authorizationService.AuthorizeAsync(
            CreateAuthenticatedUser(), FeedbackControllerPolicy);
        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task FeedbackController_DeniesFeedbackAdminWithoutAdmin()
    {
        var result = await _authorizationService.AuthorizeAsync(
            CreateUserWithRoles(FeedbackRoles.FeedbackAdmin), FeedbackControllerPolicy);
        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task FeedbackController_AllowsAdmin()
    {
        var result = await _authorizationService.AuthorizeAsync(
            CreateUserWithRoles(RoleNames.Admin), FeedbackControllerPolicy);
        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task FeedbackController_DeniesUnauthenticated()
    {
        var result = await AuthorizeAnonymousAsync(FeedbackControllerPolicy);
        result.Succeeded.Should().BeFalse();
    }

    private static string FeedbackControllerPolicy =>
        SectionType("Humans.Feedback.Controllers.FeedbackController")
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)?.Policy
        ?? throw new InvalidOperationException(
            "FeedbackController must carry a policy-bearing [Authorize] attribute.");

    /// <summary>Section controllers are internal; throws on a miss so a rename can't drop a row silently.</summary>
    private static Type SectionType(string fullName) =>
        Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    [HumansTheory]
    [InlineData(FinanceRoles.FinanceAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task FinanceAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.FinanceAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(EventsRoles.EventsAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task EventsAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.EventsAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(ConsentRoles.ConsentCoordinator, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    [InlineData(UsersRoles.UsersAdmin, false)]
    public async Task ReviewQueueAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ReviewQueueAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(ConsentRoles.ConsentCoordinator, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task ConsentCoordinatorBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ConsentCoordinatorBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(ShiftsRoles.ShiftsNoInfo, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task ShiftDashboardAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ShiftDashboardAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(ShiftsRoles.ShiftsNoInfo, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
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

    [HumansTheory]
    [InlineData(nameof(UserState.Active), true, true)]
    [InlineData(nameof(UserState.Active), false, false)]
    [InlineData(nameof(UserState.Bare), true, false)]
    [InlineData(nameof(UserState.Suspended), true, false)]
    [InlineData(nameof(UserState.AdminSuspended), true, false)]
    [InlineData(nameof(UserState.DeletePending), true, false)]
    [InlineData(nameof(UserState.Rejected), true, false)]
    [InlineData(nameof(UserState.Deleted), true, false)]
    [InlineData(nameof(UserState.Merged), true, false)]
    [InlineData(null, true, false)]
    [InlineData("Unknown", true, false)]
    [InlineData(null, false, false)]
    public async Task CityPlanningHub_RequiresActiveMembershipOnConnectAndNegotiate(
        string? state, bool authenticated, bool allowed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        foreach (var contributor in SectionDiscoveryExtensions.DiscoverImplementations<ISectionEndpoints>())
            contributor.MapEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/hubs/city-planning", StringComparison.Ordinal) == true)
            .ToList();
        endpoints.Should().HaveCount(2);
        var user = authenticated ? CreateUserWithRoles(RoleNames.Admin) : new ClaimsPrincipal(new ClaimsIdentity());
        if (state is not null)
            ((ClaimsIdentity)user.Identity!).AddClaim(new Claim(RoleAssignmentClaimsTransformation.UserStateClaimType, state));
        var provider = _serviceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var endpoint in endpoints)
        {
            var policy = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            policy.Should().NotBeNull();
            var result = await _authorizationService.AuthorizeAsync(user, resource: null, policy!);
            result.Succeeded.Should().Be(allowed, endpoint.RoutePattern.RawText);
        }
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
        var user = CreateUserWithRoles(CantinaRoles.CantinaAdmin);
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
