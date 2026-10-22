using Humans.Users.Controllers;
using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Controllers;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>Catches authorization attributes removed or changed on critical actions.</summary>
public class EndpointAuthorizationTests
{
    /// <summary>
    /// Found by the runtime's own section discovery: <c>GetReferencedAssemblies()</c> returns no sections, since Shell
    /// names no type in them. <see cref="AssertNonEmpty"/> because a sweep over nothing passes silently.
    /// </summary>
    private static IReadOnlyList<Type> AllControllerTypes()
    {
        var controllers = new[] { typeof(HomeController).Assembly }
            .Concat(SectionDiscoveryExtensions.SectionAssemblies())
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        AssertNonEmpty(controllers);
        return controllers;
    }

    /// <summary>Section controllers are internal; throws on a miss so a critical endpoint can't drop out silently.</summary>
    private static Type SectionType(string fullName) =>
        SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    private static void AssertNonEmpty(IReadOnlyCollection<Type> controllers) =>
        controllers.Should().HaveCountGreaterThan(
            50,
            "these sweeps are the repo-wide safety net for missing [Authorize] / " +
            "[ValidateAntiForgeryToken]; an anchor that resolves to few or no controllers " +
            "makes them pass vacuously");

    public static TheoryData<Type, string?, string> CriticalEndpointPolicies => new()
    {
        { typeof(UsersAdminController), "PurgeHuman", "AdminOnly" },
        { SectionType("Humans.Debug.Controllers.DebugController"), "Logs", "AdminOnly" },
        { SectionType("Humans.Debug.Controllers.DebugController"), "Configuration", "AdminOnly" },
        { SectionType("Humans.Debug.Controllers.DebugController"), "DbStats", "AdminOnly" },
        { SectionType("Humans.Debug.Controllers.DebugController"), "CacheStats", "AdminOnly" },
        { typeof(UsersAdminController), "Audience", "AdminOnly" },
        { typeof(UsersAdminAccountMergesController), null, "AdminOnly" },
        { SectionType("Humans.Email.Controllers.EmailController"), null, "AdminOnly" },
        { typeof(AdminController), "Index", "AnyAdminRole" },
        { SectionType("Humans.AuditLog.Controllers.AuditLogController"), "Index", "BoardOrAdmin" },
        { SectionType("Humans.Monitor.Controllers.MonitorController"), "CheckDriveActivity", "BoardOrAdmin" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "Resource", "BoardOrAdmin" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "Human", "HumanAdminBoardOrAdmin" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "UpdateSyncSetting", "AdminOnly" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "SyncSystemTeams", "AdminOnly" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "SyncResults", "AdminOnly" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "CheckGroupSettings", "AdminOnly" },
        { SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), "GroupSettingsResults", "AdminOnly" },
        { SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), null, "ReviewQueueAccess" },
        { SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), "Clear", "ConsentCoordinatorBoardOrAdmin" },
        { SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), "Flag", "ConsentCoordinatorBoardOrAdmin" },
        { SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), "Reject", "ConsentCoordinatorBoardOrAdmin" },
        { SectionType("Humans.Budget.Controllers.BudgetAdminController"), null, "FinanceAdminOrAdmin" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), null, "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "Index", "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "Detail", "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "PostMessage", "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "UpdateStatus", "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "UpdateAssignment", "AdminOnly" },
        { SectionType("Humans.Feedback.Controllers.FeedbackController"), "SetGitHubIssue", "AdminOnly" },
        { SectionType("Humans.Scanner.Controllers.ScannerController"), null, "ScannerAccess" },
        { SectionType("Humans.Tickets.Controllers.TicketsOnsiteAdminController"), null, "ScannerAccess" },
        { SectionType("Humans.Tickets.Controllers.TicketsGateAdminController"), null, "TicketAdminOrAdmin" },
        { SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), null, "ShiftDepartmentManager" },
        { SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), "SearchVolunteers", "ShiftDashboardAccess" },
        { SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), "Voluntell", "ShiftDashboardAccess" },
    };

    [HumansTheory]
    [MemberData(nameof(CriticalEndpointPolicies))]
    public void Critical_endpoints_require_expected_policy(Type controllerType, string? actionName, string expectedPolicy)
    {
        AssertHasPolicy(controllerType, actionName, expectedPolicy);
    }

    // Any admin-shaped role lands on the shell; sidebar items filter per item.
    [HumansFact]
    public void AdminController_Index_RequiresAnyAdminRolePolicy()
    {
        AssertHasPolicy(typeof(AdminController), "Index", "AnyAdminRole");
    }

    [HumansTheory]
    [InlineData("UpdateSyncSetting")]
    [InlineData("SyncSystemTeams")]
    [InlineData("SyncResults")]
    [InlineData("ActivityLog")]
    [InlineData("CheckGroupSettings")]
    [InlineData("GroupSettingsResults")]
    public void GoogleAdminEndpoint_RequiresAdminPolicy(string actionName)
    {
        AssertHasPolicy(SectionType("Humans.GoogleIntegration.Controllers.GoogleController"), actionName, "AdminOnly");
    }

    [HumansFact]
    public void OnboardingReviewController_RequiresReviewQueueAccess()
    {
        AssertHasPolicy(SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), null, "ReviewQueueAccess");
    }

    [HumansTheory]
    [InlineData("Clear")]
    [InlineData("Flag")]
    [InlineData("Reject")]
    public void OnboardingReviewConsentActions_RequireConsentCoordinatorBoardOrAdmin(string actionName)
    {
        AssertHasPolicy(SectionType("Humans.Onboarding.Controllers.OnboardingReviewController"), actionName, "ConsentCoordinatorBoardOrAdmin");
    }

    // Feedback is superseded by Issues: no route may create a FeedbackReport.
    [HumansFact]
    public void FeedbackController_HasNoReportCreationRoute()
    {
        var rootPosts = SectionType("Humans.Feedback.Controllers.FeedbackController")
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<HttpPostAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Template ?? string.Empty)
            .Where(t => t.Length == 0)
            .ToList();

        rootPosts.Should().BeEmpty("POST /Feedback must not exist — Feedback accepts no new reports");
    }

    [HumansFact]
    public void BackdoorFeedbackController_OnlyPostsMessages()
    {
        var postTemplates = SectionType("Humans.Backdoor.Controllers.BackdoorFeedbackController")
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<HttpPostAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Template ?? string.Empty)
            .ToList();

        postTemplates.Should().BeEquivalentTo(["{id}/messages"]);
    }

    [HumansFact]
    public void BudgetAdminController_RequiresFinanceAdminOrAdmin()
    {
        AssertHasPolicy(SectionType("Humans.Budget.Controllers.BudgetAdminController"), null, "FinanceAdminOrAdmin");
    }

    // Page entry uses the wider policy so any coordinator can land; Voluntell and SearchVolunteers narrow it per action.
    [HumansFact]
    public void ShiftDashboardController_RequiresShiftDepartmentManager()
    {
        AssertHasPolicy(SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), null, "ShiftDepartmentManager");
    }

    [HumansFact]
    public void ShiftDashboardController_SearchVolunteers_RequiresShiftDashboardAccess()
    {
        AssertHasPolicy(SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), "SearchVolunteers", "ShiftDashboardAccess");
    }

    [HumansFact]
    public void ShiftDashboardController_Voluntell_RequiresShiftDashboardAccess()
    {
        AssertHasPolicy(SectionType("Humans.Shifts.Controllers.ShiftDashboardController"), "Voluntell", "ShiftDashboardAccess");
    }

    [HumansFact]
    public void AllPostActions_HaveAntiForgeryValidation()
    {
        var controllerTypes = AllControllerTypes();

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

        var controllerTypes = AllControllerTypes()
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
            "GuestAccountController.CommunicationPreferences",
            "GuestAccountController.UpdatePreference",
            "TeamController.Index",
            "TeamController.Details",
            "TeamController.Tree",
            "DebugController.DbVersion",
            "ProfileEmailsController.VerifyEmail",
            "ProfileViewController.Picture",
            "ProfileViewController.PublicPopover",
        };

        var controllerTypes = AllControllerTypes()
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

}
