using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Onboarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Humans.Onboarding.Services;
using Humans.Onboarding.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="GuestController.Index"/> defers to
/// <see cref="IOnboardingWidgetState"/>: incomplete users are redirected into
/// the onboarding widget, complete users fall through to the guest dashboard.
/// </summary>
public class GuestControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IOnboardingWidgetState _widgetState = Substitute.For<IOnboardingWidgetState>();

    private GuestController BuildSut(User user)
    {
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user,
                [],
                [],
                profile: null)));

        var ctrl = new GuestController(
            _userService,
            _widgetState);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
        };
        ctrl.Url = Substitute.For<IUrlHelper>();
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task GuestGreeting_RendersTheCurrentEncodedIdentityInTheViewersCulture(string language)
    {
        using var culture = new CultureScope(language);
        var user = new User { Id = Guid.NewGuid(), BurnerName = "Original name" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>()).Returns(OnboardingWidgetStep.Complete);
        var controller = BuildSut(user);
        var result = Assert.IsType<ViewResult>(await controller.Index(TestContext.Current.CancellationToken));
        const string currentName = "Renamed <script>alert(1)</script> &";
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = user.Id, BurnerName = currentName }, [], [], profile: null));

        var html = await RenderDashboardAsync(result.Model!);

        var greeting = Regex.Match(html, "<h1[^>]*>(?<greeting>.*?)</h1>",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["greeting"].Value.Trim();
        var template = new ResourceManager(typeof(OnboardingResource))
            .GetString("Onboarding_GuestGreeting", CultureInfo.GetCultureInfo(language));
        Assert.NotNull(template);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, template,
            $"<span>{HtmlEncoder.Default.Encode(currentName)}</span>"), greeting);
        Assert.DoesNotContain("Original name", greeting, StringComparison.Ordinal);
    }

    private async Task<string> RenderDashboardAsync(object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GuestControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_userService);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(OnboardingResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Guest/Index.cshtml", isMainPage: false);
        Assert.True(result.Success);
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Guest";
        route.Values["action"] = "Index";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }

    [HumansFact]
    public async Task Index_RedirectsToOnboardingWidget_WhenStepNotComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Names);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    [HumansFact]
    public async Task Index_CancelsAbandonedViewerReadBeforeResolvingTheWidget()
    {
        using var request = new CancellationTokenSource();
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        var ctrl = BuildSut(user);
        ctrl.HttpContext.RequestAborted = request.Token;
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(user, [], [], null));
        });
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>()).Returns(OnboardingWidgetStep.Complete);

        Assert.IsType<ViewResult>(await ctrl.Index(request.Token));
        await request.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctrl.Index(request.Token));
        await _widgetState.Received(1).GetCurrentStepAsync(user.Id, request.Token);
    }

    [HumansFact]
    public async Task Index_RendersGuestDashboard_WhenStepComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Complete);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
    }
}
