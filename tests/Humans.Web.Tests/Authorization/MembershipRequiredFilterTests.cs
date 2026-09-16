using System.Reflection;
using System.Security.Claims;
using Humans.Web.Extensions;
using Humans.Backdoor.Contracts;
using Humans.Base.Constants;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Controllers;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies <see cref="MembershipRequiredFilter"/> routes authenticated users by their stored
/// <see cref="UserState"/>: only <see cref="UserState.Active"/> reaches a non-exempt controller;
/// <see cref="UserState.Bare"/> (and unseeded null) go to the onboarding widget;
/// <see cref="UserState.DeletePending"/> goes to the cancel-deletion screen; and the walled states
/// go to the account-status page. Exempt controllers and anonymous requests pass straight through.
/// </summary>
public class MembershipRequiredFilterTests
{

    // OnboardingWidgetController is internal to Humans.Onboarding since that section's G5,
    // so it cannot be named by typeof here. Resolved by reflection through the discovered
    // section assemblies, throwing on a miss so a rename cannot quietly drop the row.
    private static readonly Type OnboardingWidgetControllerType =
        SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType("Humans.Onboarding.Controllers.OnboardingWidgetController", throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            "Humans.Onboarding.Controllers.OnboardingWidgetController not found in any section assembly.");
    private static readonly Type UserControllerType = typeof(ProfileController).Assembly
        .GetType("Humans.Users.Controllers.UserController", throwOnError: true)!;
    [HumansFact]
    public async Task Active_user_reaches_a_non_exempt_controller()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Active);

        Assert.True(nextCalled, "Active users must reach the app");
        Assert.Null(result);
    }

    [HumansFact]
    public async Task Bare_user_routed_to_onboarding_widget()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Bare);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansFact]
    public async Task Unseeded_null_state_routed_to_onboarding_widget()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: null);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansFact]
    public async Task DeletePending_user_routed_to_cancel_deletion_screen()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.DeletePending);

        Assert.False(nextCalled);
        AssertRedirect(result, "Deletion", "User");
    }

    [HumansTheory]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.Rejected)]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task Walled_states_routed_to_account_status_page(UserState state)
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: state);

        Assert.False(nextCalled);
        AssertRedirect(result, "Status", "User");
    }

    [HumansFact]
    public async Task Exempt_onboarding_controller_passes_through_for_non_active_user()
    {
        var (result, nextCalled) = await RunAsync("OnboardingWidget", "Names", state: UserState.Bare);

        Assert.True(nextCalled, "the onboarding surface is the Bare landing target and must not redirect");
        Assert.Null(result);
    }

    [HumansTheory]
    [InlineData(UserState.Bare)]
    [InlineData(UserState.Suspended)]
    public async Task Issue_reporting_passes_through_for_non_active_user(UserState state)
    {
        var (result, nextCalled) = await RunAsync("Issues", "Submit", state: state);

        Assert.True(nextCalled, "Issues replaced Feedback as the only in-app report path — a user "
                                + "stuck in onboarding or on the status wall must still reach it");
        Assert.Null(result);
    }

    [HumansFact]
    public async Task Role_holder_does_not_bypass_state_routing()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Bare, role: RoleNames.Admin);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansTheory]
    [InlineData(UserState.Bare)]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.Rejected)]
    [InlineData(UserState.DeletePending)]
    public async Task Non_active_users_retain_profile_and_email_self_service(UserState state)
    {
        // Resolve the actual extracted actions; their controller identity is what the global
        // filter sees even though their public URLs still belong to the Profile surface.
        foreach (var (type, action) in new[]
        {
            (typeof(ProfileController), nameof(ProfileController.Index)),
            (typeof(ProfileController), nameof(ProfileController.Me)),
            (typeof(ProfileController), nameof(ProfileController.Edit)),
            (typeof(ProfileController), nameof(ProfileController.DeclareNotAttending)),
            (typeof(ProfileController), nameof(ProfileController.UndoNotAttending)),
            (typeof(ProfileController), nameof(ProfileController.MyOutbox)),
            (typeof(ProfileController), nameof(ProfileController.Privacy)),
            (typeof(ProfileController), nameof(ProfileController.DietaryMedical)),
            (typeof(ProfileController), nameof(ProfileController.CommunicationPreferences)),
            (typeof(ProfileController), nameof(ProfileController.UpdatePreference)),
            (typeof(ProfileController), nameof(ProfileController.Notifications)),
            (typeof(ProfileController), nameof(ProfileController.DownloadData)),
            (typeof(ProfileController), nameof(ProfileController.RequestDeletion)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.Emails)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.AddEmail)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.SetPrimary)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.SetEmailVisibility)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.DeleteEmail)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.SetGoogle)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.ClearGoogle)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.ClearPrimary)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.Link)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.Unlink)),
            (typeof(ProfileEmailsController), nameof(ProfileEmailsController.UnlinkLinkedAccount)),
        })
        {
            foreach (var method in type.GetMethods().Where(m => string.Equals(m.Name, action, StringComparison.Ordinal)))
            {
                var (result, nextCalled) = await RunAsync(
                    type.Name[..^"Controller".Length], action, state, actionMethod: method);
                Assert.True(nextCalled, $"{state} must still reach {type.Name}.{action}");
                Assert.Null(result);
            }
        }
    }

    [HumansTheory]
    [InlineData(UserState.Deleted, false)]
    [InlineData(UserState.Deleted, true)]
    [InlineData(UserState.Merged, false)]
    [InlineData(UserState.Merged, true)]
    public async Task Terminal_accounts_cannot_use_recovery_exemptions(UserState state, bool hasNames)
    {
        // Exercise both filters in their registered order. Anonymization clears names;
        // it must never send a tombstone back through the editable onboarding form.
        foreach (var type in new[]
        {
            typeof(ProfileController), typeof(ProfileEmailsController),
            OnboardingWidgetControllerType, UserControllerType,
        })
        {
            var methods = type.GetMethods().Where(m => m.DeclaringType == type
                && !m.IsDefined(typeof(AllowAnonymousAttribute), true)
                && !(type == UserControllerType && string.Equals(m.Name, "Status", StringComparison.Ordinal)));
            foreach (var method in methods)
            {
                var (result, nextCalled) = await RunAsync(
                    type.Name[..^"Controller".Length], method.Name, state,
                    role: RoleNames.Admin, actionMethod: method, nameGateHasNames: hasNames);

                Assert.False(nextCalled, $"{state} must not reach {method}");
                AssertRedirect(result, "Status", "User");
            }
        }
    }

    [HumansTheory]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task Terminal_accounts_can_reach_status_and_session_routes_without_names(UserState state)
    {
        foreach (var (controller, action) in new[]
        {
            ("User", "Status"),
            ("Account", nameof(AccountController.Logout)),
            ("Language", nameof(LanguageController.SetLanguage)),
            ("ProfileView", nameof(ProfileViewController.PublicPopover)),
            ("ProfileEmails", nameof(ProfileEmailsController.VerifyEmail)),
        })
        {
            var (result, nextCalled) = await RunAsync(
                controller, action, state, nameGateHasNames: false);

            Assert.True(nextCalled, $"{state} must still reach {controller}.{action}");
            Assert.Null(result);
        }
    }

    [HumansTheory]
    [InlineData(UserState.Bare, "ProfileEmails", "Index", "OnboardingWidget")]
    [InlineData(UserState.Suspended, "ProfileEmails", "Status", "User")]
    [InlineData(UserState.AdminSuspended, "ProfileEmails", "Status", "User")]
    [InlineData(UserState.Rejected, "ProfileEmails", "Status", "User")]
    [InlineData(UserState.Deleted, "ProfileEmails", "Status", "User")]
    [InlineData(UserState.Merged, "ProfileEmails", "Status", "User")]
    [InlineData(UserState.DeletePending, "ProfileEmails", "Deletion", "User")]
    [InlineData(UserState.Bare, "ProfileView", "Index", "OnboardingWidget")]
    [InlineData(UserState.Suspended, "ProfileView", "Status", "User")]
    [InlineData(UserState.AdminSuspended, "ProfileView", "Status", "User")]
    [InlineData(UserState.Rejected, "ProfileView", "Status", "User")]
    [InlineData(UserState.Deleted, "ProfileView", "Status", "User")]
    [InlineData(UserState.Merged, "ProfileView", "Status", "User")]
    [InlineData(UserState.DeletePending, "ProfileView", "Deletion", "User")]
    public async Task Non_active_users_cannot_view_other_profiles_send_messages_or_administer_emails(
        UserState state, string controllerName, string redirectAction, string redirectController)
    {
        var actions = controllerName switch
        {
            "ProfileEmails" => typeof(ProfileEmailsController).GetMethods()
                .Where(m => m.Name.StartsWith("Admin", StringComparison.Ordinal)).ToList(),
            _ => typeof(ProfileViewController).GetMethods()
                .Where(m => m.DeclaringType == typeof(ProfileViewController)
                    && !m.IsDefined(typeof(AllowAnonymousAttribute), true)).ToList(),
        };
        Assert.NotEmpty(actions);

        foreach (var method in actions)
            foreach (var role in new[] { null, RoleNames.Admin, RoleNames.HumanAdmin, RoleNames.Board })
            {
                var (result, nextCalled) = await RunAsync(
                    method.DeclaringType!.Name[..^"Controller".Length], method.Name, state,
                    role: role, actionMethod: method);

                Assert.False(nextCalled, $"{state} ({role ?? "member"}) must not reach {method}");
                AssertRedirect(result, redirectAction, redirectController);
            }
    }

    [HumansFact]
    public async Task Active_users_reach_profile_action_authorization()
    {
        foreach (var method in typeof(ProfileEmailsController).GetMethods()
            .Where(m => m.Name.StartsWith("Admin", StringComparison.Ordinal))
            .Concat(typeof(ProfileViewController).GetMethods()
                .Where(m => m.DeclaringType == typeof(ProfileViewController)
                    && !m.IsDefined(typeof(AllowAnonymousAttribute), true))))
        {
            var (result, nextCalled) = await RunAsync(
                method.DeclaringType!.Name[..^"Controller".Length], method.Name,
                UserState.Active, actionMethod: method);

            Assert.True(nextCalled, $"Active users must reach {method}'s role/ownership checks");
            Assert.Null(result);
        }
    }

    [HumansTheory]
    [InlineData("ProfileEmails", nameof(ProfileEmailsController.VerifyEmail))]
    [InlineData("ProfileView", nameof(ProfileViewController.Picture))]
    [InlineData("ProfileView", nameof(ProfileViewController.PublicPopover))]
    public async Task Public_profile_actions_remain_reachable_for_suspended_users(string controller, string action)
    {
        var (result, nextCalled) = await RunAsync(controller, action, UserState.AdminSuspended);

        Assert.True(nextCalled);
        Assert.Null(result);
    }

    [HumansFact]
    public async Task Anonymous_request_passes_through()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: null, authenticated: false);

        Assert.True(nextCalled);
        Assert.Null(result);
    }

    private static async Task<(IActionResult? Result, bool NextCalled)> RunAsync(
        string controllerName,
        string actionName,
        UserState? state,
        bool authenticated = true,
        string? role = null,
        string authenticationType = "test",
        MethodInfo? actionMethod = null,
        bool? nameGateHasNames = null)
    {
        var sut = new MembershipRequiredFilter();
        var ctx = BuildExecutingContext(controllerName, actionName, authenticated, state, role, authenticationType, actionMethod);
        var nextCalled = false;

        Task<ActionExecutedContext> ExecuteAction()
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        }

        if (nameGateHasNames is { } hasNames)
        {
            var users = Substitute.For<IUserServiceRead>();
            var profile = UserFixtures.Profile(
                burnerName: hasNames ? "Member" : "", firstName: "Deleted", lastName: "User");
            var info = UserInfo.Create(
                new User { Id = Guid.NewGuid(), State = state!.Value },
                [], [], [], profile, []);
            users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(info);
            var nameFilter = new NameRequiredFilter(users);
            await nameFilter.OnActionExecutionAsync(ctx, async () =>
            {
                await sut.OnActionExecutionAsync(ctx, ExecuteAction);
                return null!;
            });
        }
        else
        {
            await sut.OnActionExecutionAsync(ctx, ExecuteAction);
        }

        return (ctx.Result, nextCalled);
    }

    /// <summary>
    /// A Backdoor API key never passes through claims transformation, so its principal carries no
    /// state claim. The gate must let it through rather than redirect a JSON client to the
    /// onboarding page (nobodies-collective/Humans#1128).
    /// </summary>
    [HumansFact]
    public async Task Backdoor_key_principal_reaches_a_non_exempt_controller_without_a_state_claim()
    {
        var (result, nextCalled) = await RunAsync(
            "Home", "Index", state: null, authenticationType: BackdoorAuthentication.SchemeName);

        Assert.True(nextCalled, "Machine requests must not be routed to onboarding");
        Assert.Null(result);
    }

    private static void AssertRedirect(IActionResult? result, string action, string controller)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(action, redirect.ActionName);
        Assert.Equal(controller, redirect.ControllerName);
    }

    private static ActionExecutingContext BuildExecutingContext(
        string controllerName,
        string actionName,
        bool authenticated,
        UserState? state,
        string? role,
        string authenticationType,
        MethodInfo? actionMethod)
    {
        ClaimsIdentity identity;
        if (authenticated)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
            if (state is { } s)
            {
                claims.Add(new Claim(RoleAssignmentClaimsTransformation.UserStateClaimType, s.ToString()));
            }
            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
            identity = new ClaimsIdentity(claims, authenticationType);
        }
        else
        {
            identity = new ClaimsIdentity();
        }

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };

        // Use a real controller type so the action descriptor resolves a valid
        // ControllerTypeInfo — required for the AllowAnonymous reflection check.
        var controllerType = controllerName switch
        {
            "OnboardingWidget" => OnboardingWidgetControllerType,
            "Profile" => typeof(ProfileController),
            "ProfileEmails" => typeof(ProfileEmailsController),
            "ProfileView" => typeof(ProfileViewController),
            "Account" => typeof(AccountController),
            "Language" => typeof(LanguageController),
            "User" => UserControllerType,
            _ => typeof(HomeController),
        };
        var actionDescriptor = new ControllerActionDescriptor
        {
            ControllerName = controllerName,
            ActionName = actionName,
            ControllerTypeInfo = controllerType.GetTypeInfo(),
            MethodInfo = actionMethod ?? controllerType.GetMethods()
                .FirstOrDefault(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
                ?? typeof(MembershipRequiredFilterTests).GetMethod(nameof(BuildExecutingContext),
                    BindingFlags.NonPublic | BindingFlags.Static)!,
        };

        var actionContext = new ActionContext(
            http,
            new RouteData(),
            actionDescriptor);

        // Provide a fake controller instance — the filter only reads its
        // ControllerContext.ActionDescriptor.ControllerName.
        var stubController = new StubController
        {
            ControllerContext = new ControllerContext(actionContext),
        };

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(StringComparer.Ordinal),
            controller: stubController);
    }

    private sealed class StubController : Controller;
}
