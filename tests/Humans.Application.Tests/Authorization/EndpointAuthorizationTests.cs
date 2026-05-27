using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Verifies that critical controller actions have the correct authorization attributes.
/// Catches silent authorization regressions when attributes are accidentally removed or changed.
/// </summary>
public class EndpointAuthorizationTests
{
    public static TheoryData<Type, string?, string> CriticalEndpointPolicies => new()
    {
        { typeof(AdminController), "PurgeHuman", "AdminOnly" },
        { typeof(AdminController), "Logs", "AdminOnly" },
        { typeof(AdminController), "Configuration", "AdminOnly" },
        { typeof(AdminController), "DbStats", "AdminOnly" },
        { typeof(AdminController), "CacheStats", "AdminOnly" },
        { typeof(AdminMergeController), null, "AdminOnly" },
        { typeof(EmailController), null, "AdminOnly" },
        { typeof(AdminController), "Index", "AnyAdminRole" },
        { typeof(AuditLogController), "Index", "BoardOrAdmin" },
        { typeof(AuditLogController), "CheckDriveActivity", "BoardOrAdmin" },
        { typeof(AuditLogController), "Resource", "BoardOrAdmin" },
        { typeof(AuditLogController), "Human", "HumanAdminBoardOrAdmin" },
        { typeof(GoogleController), "SyncSettings", "AdminOnly" },
        { typeof(GoogleController), "UpdateSyncSetting", "AdminOnly" },
        { typeof(GoogleController), "SyncSystemTeams", "AdminOnly" },
        { typeof(GoogleController), "SyncResults", "AdminOnly" },
        { typeof(GoogleController), "CheckGroupSettings", "AdminOnly" },
        { typeof(GoogleController), "GroupSettingsResults", "AdminOnly" },
        { typeof(OnboardingReviewController), null, "ReviewQueueAccess" },
        { typeof(OnboardingReviewController), "Clear", "ConsentCoordinatorBoardOrAdmin" },
        { typeof(OnboardingReviewController), "Flag", "ConsentCoordinatorBoardOrAdmin" },
        { typeof(OnboardingReviewController), "Reject", "ConsentCoordinatorBoardOrAdmin" },
        { typeof(FinanceController), null, "FinanceAdminOrAdmin" },
        { typeof(ScannerController), null, "TicketAdminBoardOrAdmin" },
        { typeof(ShiftDashboardController), null, "ShiftDepartmentManager" },
        { typeof(ShiftDashboardController), "SearchVolunteers", "ShiftDashboardAccess" },
        { typeof(ShiftDashboardController), "Voluntell", "ShiftDashboardAccess" },
    };

    [HumansTheory]
    [MemberData(nameof(CriticalEndpointPolicies))]
    public void Critical_endpoints_require_expected_policy(Type controllerType, string? actionName, string expectedPolicy)
    {
        AssertHasPolicy(controllerType, actionName, expectedPolicy);
    }

    // The /Admin dashboard itself is reachable by any admin-shaped role so
    // domain admins (FinanceAdmin etc.) can land on the shell. Sidebar items
    // inside still filter per-item.
    [HumansFact]
    public void AdminController_Index_RequiresAnyAdminRolePolicy()
    {
        AssertHasPolicy(typeof(AdminController), "Index", "AnyAdminRole");
    }

    // --- Google admin endpoints must require Admin ---

    [HumansTheory]
    [InlineData("SyncSettings")]
    [InlineData("UpdateSyncSetting")]
    [InlineData("SyncSystemTeams")]
    [InlineData("SyncResults")]
    [InlineData("CheckGroupSettings")]
    [InlineData("GroupSettingsResults")]
    public void GoogleAdminEndpoint_RequiresAdminPolicy(string actionName)
    {
        AssertHasPolicy(typeof(GoogleController), actionName, "AdminOnly");
    }

    // --- Onboarding review endpoints ---

    [HumansFact]
    public void OnboardingReviewController_RequiresReviewQueueAccess()
    {
        AssertHasPolicy(typeof(OnboardingReviewController), null, "ReviewQueueAccess");
    }

    [HumansTheory]
    [InlineData("Clear")]
    [InlineData("Flag")]
    [InlineData("Reject")]
    public void OnboardingReviewConsentActions_RequireConsentCoordinatorBoardOrAdmin(string actionName)
    {
        AssertHasPolicy(typeof(OnboardingReviewController), actionName, "ConsentCoordinatorBoardOrAdmin");
    }

    // --- Finance endpoints ---

    [HumansFact]
    public void FinanceController_RequiresFinanceAdminOrAdmin()
    {
        AssertHasPolicy(typeof(FinanceController), null, "FinanceAdminOrAdmin");
    }

    // --- Event guide admin endpoints ---

    [HumansFact]
    public void EventsAdminController_RequiresEventsAdminOrAdminPolicy()
    {
        AssertHasPolicy(typeof(EventsAdminController), null, "EventsAdminOrAdmin");
    }

    // --- Shift dashboard endpoints ---

    // Page entry uses the WIDER policy so any team coordinator / sub-team manager
    // can land on the dashboard. Privileged actions (Voluntell / SearchVolunteers)
    // override at the action level with the NARROWER ShiftDashboardAccess.
    [HumansFact]
    public void ShiftDashboardController_RequiresShiftDepartmentManager()
    {
        AssertHasPolicy(typeof(ShiftDashboardController), null, "ShiftDepartmentManager");
    }

    [HumansFact]
    public void ShiftDashboardController_SearchVolunteers_RequiresShiftDashboardAccess()
    {
        AssertHasPolicy(typeof(ShiftDashboardController), "SearchVolunteers", "ShiftDashboardAccess");
    }

    [HumansFact]
    public void ShiftDashboardController_Voluntell_RequiresShiftDashboardAccess()
    {
        AssertHasPolicy(typeof(ShiftDashboardController), "Voluntell", "ShiftDashboardAccess");
    }

    // --- POST actions must have ValidateAntiForgeryToken ---

    [HumansFact]
    public void AllPostActions_HaveAntiForgeryValidation()
    {
        var controllerTypes = typeof(HumansControllerBase).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract);

        var violations = new List<string>();

        foreach (var controller in controllerTypes)
        {
            var hasControllerLevelAutoValidation = controller.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>() != null;
            var hasControllerLevelValidation = controller.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null;

            if (hasControllerLevelAutoValidation || hasControllerLevelValidation)
                continue;

            var postActions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<HttpPostAttribute>() != null);

            foreach (var action in postActions)
            {
                var hasActionValidation = action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null;
                var hasIgnoreValidation = action.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() != null;

                if (!hasActionValidation && !hasIgnoreValidation)
                {
                    violations.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        violations.Should().BeEmpty(
            "all POST actions should have [ValidateAntiForgeryToken] or [AutoValidateAntiforgeryToken] at controller level. Violations: " +
            string.Join(", ", violations));
    }

    [HumansFact]
    public void AllControllerActions_HaveAuthorizeOrAllowAnonymous()
    {
        var anonymousControllers = new HashSet<string>(StringComparer.Ordinal)
        {
            "HomeController",
            "AccountController",
            "DevLoginController",
            "ApiController",
            "VersionController",
            "AboutController",
            "LanguageController",
            "UnsubscribeController"
        };

        var controllerTypes = typeof(HumansControllerBase).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => !anonymousControllers.Contains(t.Name));

        var violations = new List<string>();

        foreach (var controller in controllerTypes)
        {
            var hasControllerAuth = controller.GetCustomAttribute<AuthorizeAttribute>() != null;
            var hasControllerAllowAnon = controller.GetCustomAttribute<AllowAnonymousAttribute>() != null;

            if (hasControllerAuth || hasControllerAllowAnon)
                continue;

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<HttpGetAttribute>() != null ||
                            m.GetCustomAttribute<HttpPostAttribute>() != null ||
                            m.GetCustomAttribute<HttpPutAttribute>() != null ||
                            m.GetCustomAttribute<HttpDeleteAttribute>() != null ||
                            m.GetCustomAttribute<RouteAttribute>() != null);

            foreach (var action in actions)
            {
                var hasActionAuth = action.GetCustomAttribute<AuthorizeAttribute>() != null;
                var hasActionAllowAnon = action.GetCustomAttribute<AllowAnonymousAttribute>() != null;

                if (!hasActionAuth && !hasActionAllowAnon)
                {
                    violations.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        violations.Should().BeEmpty(
            "all controller actions (except intentionally anonymous ones) should have [Authorize] or [AllowAnonymous]. " +
            "Unprotected: " + string.Join(", ", violations));
    }

    [HumansFact]
    public void AllowAnonymousOnAuthorizedControllers_IsExplicitlyAllowlisted()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "GuestController.CommunicationPreferences",
            "GuestController.UpdatePreference",
            "TeamController.Index",
            "TeamController.Details",
            "AdminController.DbVersion",
            "ProfileController.VerifyEmail",
            "ProfileController.Picture",
        };

        var controllerTypes = typeof(HumansControllerBase).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() != null);

        var violations = new List<string>();

        foreach (var controller in controllerTypes)
        {
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null);

            foreach (var action in actions)
            {
                var key = $"{controller.Name}.{action.Name}";
                if (!allowlist.Contains(key))
                {
                    violations.Add(key);
                }
            }
        }

        violations.Should().BeEmpty(
            "[AllowAnonymous] on an [Authorize] controller must be explicitly allowlisted in this test. " +
            "New anonymous endpoints: " + string.Join(", ", violations));
    }

    [HumansFact]
    public void ScannerController_Remains_ClientOnly_GetSurface()
    {
        var constructor = typeof(ScannerController).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().ContainSingle()
            .Subject;
        constructor.GetParameters().Should().BeEmpty(
            "Scanner is documented as a browser-only section with no server-side service, repository, or cache dependencies");

        var actions = typeof(ScannerController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        actions.Should().OnlyContain(
            m => m.GetCustomAttribute<HttpGetAttribute>() != null,
            "Scanner endpoints must not write server-side state");
        actions.Should().OnlyContain(
            m => m.GetCustomAttribute<HttpPostAttribute>() == null &&
                 m.GetCustomAttribute<HttpPutAttribute>() == null &&
                 m.GetCustomAttribute<HttpDeleteAttribute>() == null &&
                 m.GetCustomAttribute<HttpPatchAttribute>() == null,
            "the current Scanner section is explicitly not a check-in or persistence gateway");
    }

    private static void AssertHasPolicy(Type controllerType, string? actionName, string expectedPolicy)
    {
        AuthorizeAttribute? attr;

        if (actionName is null)
        {
            attr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        }
        else
        {
            var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
                .ToList();

            methods.Should().NotBeEmpty($"action '{actionName}' should exist on {controllerType.Name}");

            attr = methods
                .Select(m => m.GetCustomAttribute<AuthorizeAttribute>())
                .FirstOrDefault(a => a is not null);

            attr ??= controllerType.GetCustomAttribute<AuthorizeAttribute>();
        }

        attr.Should().NotBeNull(
            $"{controllerType.Name}{(actionName is not null ? "." + actionName : "")} should have [Authorize]");
        attr.Policy.Should().Be(expectedPolicy,
            $"{controllerType.Name}{(actionName is not null ? "." + actionName : "")} should have Policy='{expectedPolicy}'");
    }

    private static AuthorizeAttribute? GetAuthorizeAttribute(Type controllerType, string? actionName)
    {
        if (actionName is null)
        {
            return controllerType.GetCustomAttribute<AuthorizeAttribute>();
        }

        var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
            .ToList();

        methods.Should().NotBeEmpty($"action '{actionName}' should exist on {controllerType.Name}");

        return methods
            .Select(m => m.GetCustomAttribute<AuthorizeAttribute>())
            .FirstOrDefault(a => a is not null)
            ?? controllerType.GetCustomAttribute<AuthorizeAttribute>();
    }
}
