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
    // --- Admin endpoints must require Admin role ---

    [HumansTheory]
    [InlineData(typeof(AdminController), "Index")]
    [InlineData(typeof(AdminController), "PurgeHuman")]
    [InlineData(typeof(AdminController), "Logs")]
    [InlineData(typeof(AdminController), "Configuration")]
    [InlineData(typeof(AdminController), "DbStats")]
    [InlineData(typeof(AdminController), "CacheStats")]
    [InlineData(typeof(AdminMergeController), null)] // class-level
    [InlineData(typeof(EmailController), null)] // class-level
    public void AdminEndpoint_RequiresAdminPolicy(Type controllerType, string? actionName)
    {
        AssertHasPolicy(controllerType, actionName, "AdminOnly");
    }

    // --- Board endpoints must require Board or Admin ---

    [HumansTheory]
    [InlineData(typeof(BoardController), null)] // class-level
    public void BoardEndpoint_RequiresBoardOrAdminPolicy(Type controllerType, string? actionName)
    {
        AssertHasPolicy(controllerType, actionName, "BoardOrAdmin");
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

    // --- POST actions must have ValidateAntiForgeryToken ---

    [HumansFact]
    public void AllPostActions_HaveAntiForgeryValidation()
    {
        var controllerTypes = typeof(HumansControllerBase).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract);

        var violations = new List<string>();

        foreach (var controller in controllerTypes)
        {
            // Check for controller-level auto-validation
            var hasControllerLevelAutoValidation = controller.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>() != null;

            // Check if class-level ValidateAntiForgeryToken is applied
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

    // --- Authorize attribute coverage ---

    [HumansFact]
    public void AllControllerActions_HaveAuthorizeOrAllowAnonymous()
    {
        // Controllers that are intentionally anonymous (public-facing pages)
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

            // Check each action
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

    // --- AllowAnonymous scope guardrail ---

    /// <summary>
    /// Guards against accidental scope creep of [AllowAnonymous] on controllers that
    /// have [Authorize] at class level. Any new anonymous endpoint on an authorized
    /// controller must be explicitly added to this allowlist with a justification.
    /// </summary>
    [HumansFact]
    public void AllowAnonymousOnAuthorizedControllers_IsExplicitlyAllowlisted()
    {
        // Allowlist: controller.action → why it's anonymous
        // When adding a new entry, include a comment explaining why it needs anonymous access.
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "GuestController.CommunicationPreferences",  // unsubscribe token passthrough (no session created)
            "GuestController.UpdatePreference",           // AJAX updates from token-scoped comms prefs page
            "TeamController.Index",                       // public team directory
            "TeamController.Details",                     // public team detail page
            "AdminController.DbVersion",                  // health check endpoint
            "ProfileController.VerifyEmail",              // email verification link from email
        };

        // Also include any actions we discover that are already [AllowAnonymous] in ProfileController etc.
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

    // --- Helpers ---

    private static void AssertHasPolicy(Type controllerType, string? actionName, string expectedPolicy)
    {
        AuthorizeAttribute? attr;

        if (actionName is null)
        {
            // Check class-level authorization
            attr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        }
        else
        {
            // Check action-level authorization (may have overloads, find any match)
            var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
                .ToList();

            methods.Should().NotBeEmpty($"action '{actionName}' should exist on {controllerType.Name}");

            attr = methods
                .Select(m => m.GetCustomAttribute<AuthorizeAttribute>())
                .FirstOrDefault(a => a is not null);

            // If not on the action, check class-level
            attr ??= controllerType.GetCustomAttribute<AuthorizeAttribute>();
        }

        attr.Should().NotBeNull(
            $"{controllerType.Name}{(actionName is not null ? "." + actionName : "")} should have [Authorize]");
        attr!.Policy.Should().Be(expectedPolicy,
            $"{controllerType.Name}{(actionName is not null ? "." + actionName : "")} should have Policy='{expectedPolicy}'");
    }
}
