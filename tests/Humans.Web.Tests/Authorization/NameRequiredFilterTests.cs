using System.Reflection;
using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies the onboarding name-gate (<see cref="NameRequiredFilter"/>): authenticated
/// users with a blank BurnerName are redirected to the name form, while named users,
/// unauthenticated requests, the gate page itself, and allow-listed routes pass through.
/// Covers redirect-loop safety for nobodies-collective/Humans#812.
/// </summary>
public class NameRequiredFilterTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [HumansFact]
    public async Task Unauthenticated_PassesThrough()
    {
        var users = Substitute.For<IUserServiceRead>();
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext("Home", "Index", authenticated: false);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.True(nextCalled);
        Assert.Null(ctx.Result);
        await users.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AuthenticatedWithBlankBurnerName_RedirectsToNameForm()
    {
        var users = StubUserInfo(burnerName: "");
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext("Home", "Index", authenticated: true);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.False(nextCalled, "Gate must short-circuit a nameless user");
        var redirect = Assert.IsType<RedirectToActionResult>(ctx.Result);
        Assert.Equal("Names", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    [HumansFact]
    public async Task AuthenticatedWithValidBurnerName_PassesThrough()
    {
        var users = StubUserInfo(burnerName: "Sparkle");
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext("Home", "Index", authenticated: true);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.True(nextCalled, "A named user must never be redirected");
        Assert.Null(ctx.Result);
    }

    [HumansFact]
    public async Task NoProfile_RedirectsToNameForm()
    {
        // OAuth/import stub: User exists, Profile is null → not yet named.
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(profile: null)));
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext("Home", "Index", authenticated: true);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.False(nextCalled);
        Assert.IsType<RedirectToActionResult>(ctx.Result);
    }

    [HumansTheory]
    [InlineData("OnboardingWidget", "Names")]
    [InlineData("Account", "Logout")]
    [InlineData("Account", "ExternalLoginCallback")]
    [InlineData("Home", "Error")]
    [InlineData("Home", "Privacy")]
    public async Task AllowListedRoutes_PassThrough_EvenWithBlankBurnerName(
        string controllerName, string actionName)
    {
        var users = StubUserInfo(burnerName: "");
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext(controllerName, actionName, authenticated: true);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.True(nextCalled,
            $"{controllerName}/{actionName} must be reachable so the gate cannot loop or trap the user");
        Assert.Null(ctx.Result);
    }

    // Regression for the onboarding-shift bypass (Codex review on #812): only the
    // Names form is exempt, so every other OnboardingWidget route — the dispatcher,
    // and the shift-signup/consent POSTs that don't all re-check names — must
    // redirect a nameless user to the form rather than let them act. Before this,
    // the whole controller was exempt and a nameless user could POST
    // OnboardingWidget/SignUp to create a rota signup with a blank-name profile.
    [HumansTheory]
    [InlineData("OnboardingWidget", "Index")]
    [InlineData("OnboardingWidget", "Shifts")]
    [InlineData("OnboardingWidget", "SignUp")]
    [InlineData("OnboardingWidget", "SignUpRange")]
    [InlineData("OnboardingWidget", "Skip")]
    [InlineData("OnboardingWidget", "Consents")]
    [InlineData("OnboardingWidget", "SignConsent")]
    public async Task OnboardingWidgetNonNameRoutes_RedirectNamelessUserToNameForm(
        string controllerName, string actionName)
    {
        var users = StubUserInfo(burnerName: "");
        var sut = new NameRequiredFilter(users);
        var ctx = BuildContext(controllerName, actionName, authenticated: true);

        var nextCalled = await RunAsync(sut, ctx);

        Assert.False(nextCalled,
            $"{controllerName}/{actionName} must gate a nameless user — only the name form is exempt");
        var redirect = Assert.IsType<RedirectToActionResult>(ctx.Result);
        Assert.Equal("Names", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    private static async Task<bool> RunAsync(NameRequiredFilter sut, ActionExecutingContext ctx)
    {
        var nextCalled = false;
        await sut.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return nextCalled;
    }

    private static IUserServiceRead StubUserInfo(string burnerName)
    {
        var users = Substitute.For<IUserServiceRead>();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            BurnerName = burnerName,
            FirstName = string.IsNullOrWhiteSpace(burnerName) ? "" : "Jane",
            LastName = string.IsNullOrWhiteSpace(burnerName) ? "" : "Doe",
            State = string.IsNullOrWhiteSpace(burnerName) ? ProfileState.Stub : ProfileState.Active,
            CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
        };
        users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(profile)));
        return users;
    }

    private static UserInfo MakeUserInfo(Profile? profile) =>
        UserInfo.Create(
            new User { Id = UserId, PreferredLanguage = "en" },
            [], [], [],
            profile: profile,
            [], [], [], []);

    private static ActionExecutingContext BuildContext(
        string controllerName, string actionName, bool authenticated)
    {
        var identity = authenticated
            ? new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) },
                authenticationType: "test")
            : new ClaimsIdentity();

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        var controllerType = controllerName switch
        {
            "OnboardingWidget" => typeof(OnboardingWidgetController),
            "Account" => typeof(AccountController),
            _ => typeof(HomeController),
        };

        var actionDescriptor = new ControllerActionDescriptor
        {
            ControllerName = controllerName,
            ActionName = actionName,
            ControllerTypeInfo = controllerType.GetTypeInfo(),
            MethodInfo = controllerType.GetMethods()
                .FirstOrDefault(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
                ?? typeof(NameRequiredFilterTests).GetMethod(nameof(BuildContext),
                    BindingFlags.NonPublic | BindingFlags.Static)!,
        };

        var actionContext = new ActionContext(http, new RouteData(), actionDescriptor);
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
