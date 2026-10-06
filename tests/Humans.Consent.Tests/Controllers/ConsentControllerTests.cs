using System.Globalization;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Consent.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Consent.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Consent.Tests.Controllers;

/// <summary>
/// Gates: <c>Review</c> (GET) and <c>Submit</c> (POST) redirect Stub-state
/// profiles (null legal name) to <c>/Profile/Me/Edit</c> rather than rendering
/// the signing form or persisting a <c>ConsentRecord</c>.
/// </summary>
public sealed class ConsentControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly IOnboardingIntake _onboardingService = Substitute.For<IOnboardingIntake>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ConsentResource> _localizer =
        Substitute.For<IStringLocalizer<ConsentResource>>();
    private readonly DefaultHttpContext _http = new();

    public ConsentControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private ConsentController BuildSut(Guid userId)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _http.RequestServices = services.BuildServiceProvider();
        _http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        var ctrl = new ConsentController(
            _userService,
            _consentService,
            _onboardingService,
            _localizer,
            NullLogger<ConsentController>.Instance);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = "Test" },
        };
        ctrl.TempData = new TempDataDictionary(_http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static ProfileInfo StubProfile() => UserFixtures.Profile();

    private static ProfileInfo ActiveProfile() => UserFixtures.Profile(
        burnerName: "Burner",
        firstName: "First",
        lastName: "Last");

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = UserFixtures.StateFor(profile),
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        communicationPreferences: []);

    [HumansFact]
    public async Task Index_RendersPendingRowsFirstUtcDatesAndTenHistoryItems()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, ActiveProfile()));
        var stamp = Instant.FromUtc(2026, 1, 31, 23, 30);
        ConsentDashboardDocument Document(string name, bool signed) => new(
            Guid.NewGuid(), name, "v1", stamp, signed, signed ? stamp : null, "Changes", stamp);
        _consentService.GetConsentDashboardAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ConsentDashboard([
                new(Guid.NewGuid(), "Z-complete-team", [Document("Completed-document", true)]),
                new(Guid.NewGuid(), "B-pending-team", [Document("Other-document", false)]),
                new(Guid.NewGuid(), "A-pending-team", [Document("0-signed-document", true),
                    Document("Zulu-pending-document", false), Document("Alpha-pending-document", false)])
            ], Enumerable.Range(0, 11).Select(i => new ConsentDashboardHistoryItem(
                Guid.NewGuid(), $"History{i:D2}", "v1", stamp)).ToList()));

        var result = Assert.IsType<ViewResult>(await BuildSut(userId).Index());
        var html = await RenderIndexAsync(Assert.IsType<ConsentIndexViewModel>(result.Model));

        Assert.True(html.IndexOf("A-pending-team", StringComparison.Ordinal) < html.IndexOf("B-pending-team", StringComparison.Ordinal));
        Assert.True(html.IndexOf("B-pending-team", StringComparison.Ordinal) < html.IndexOf("Z-complete-team", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Alpha-pending-document", StringComparison.Ordinal) < html.IndexOf("Zulu-pending-document", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Zulu-pending-document", StringComparison.Ordinal) < html.IndexOf("0-signed-document", StringComparison.Ordinal));
        Assert.Contains("Jan 31, 2026", html);
        Assert.Contains("Jan 31, 2026 23:30", html);
        Assert.DoesNotContain("Feb 1, 2026", html);
        Assert.Contains("History09", html);
        Assert.DoesNotContain("History10", html);
    }

    private static async Task<string> RenderIndexAsync(ConsentIndexViewModel model)
    {
        using var culture = new CultureScope("en");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ConsentControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ConsentResource).Assembly);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var engine = services.GetRequiredService<IRazorViewEngine>();
        var result = engine.GetView(null, "/Views/Consent/Index.cshtml", isMainPage: false);
        Assert.True(result.Success, string.Join(", ", result.SearchedLocations ?? []));
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Consent";
        route.Values["action"] = "Index";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View.RenderAsync(context);
        return writer.ToString();
    }

    [HumansTheory]
    [InlineData("IndexViewer")]
    [InlineData("IndexDashboard")]
    [InlineData("ReviewViewer")]
    [InlineData("ReviewProfile")]
    [InlineData("ReviewDetail")]
    public async Task ConsentPages_StopLoadingAfterRequestCancellation(string boundary)
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _http.RequestAborted = request.Token;
        var abandon = false;
        var reads = 0;
        async ValueTask<UserInfo?> ReadUser(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            reads++;
            if (abandon && ((reads == 1 && (string.Equals(boundary, "IndexDashboard", StringComparison.Ordinal)
                    || string.Equals(boundary, "ReviewProfile", StringComparison.Ordinal)))
                || (reads == 2 && string.Equals(boundary, "ReviewDetail", StringComparison.Ordinal))))
                await request.CancelAsync();
            return WrapInUserInfo(userId, ActiveProfile());
        }
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call => ReadUser(call.Arg<CancellationToken>()));
        _consentService.GetConsentDashboardAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ConsentDashboard([], []);
        });
        _consentService.GetConsentReviewDetailAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return (ConsentReviewDetail?)null;
        });
        var index = boundary.StartsWith("Index", StringComparison.Ordinal);
        Func<Task<IActionResult>> load = index ? ctrl.Index : () => ctrl.Review(Guid.NewGuid());
        if (index) Assert.IsType<ViewResult>(await load());
        else Assert.IsType<NotFoundResult>(await load());
        reads = 0;
        abandon = true;
        if (boundary.EndsWith("Viewer", StringComparison.Ordinal))
            await request.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(load);
    }

    [HumansFact]
    public async Task Review_Get_StubProfile_RedirectsToProfileEdit()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, StubProfile()));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Review(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Profile", redirect.ControllerName);
        // Service must NOT be called when gated.
        await _consentService.DidNotReceive().GetConsentReviewDetailAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Submit_Post_StubProfile_RedirectsToProfileEdit_AndDoesNotSubmit()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, StubProfile()));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Submit(new ConsentSubmitModel
        {
            DocumentVersionId = Guid.NewGuid(),
            ExplicitConsent = true,
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Profile", redirect.ControllerName);
        await _consentService.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Review_Get_ActiveProfile_DoesNotRedirect()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(userId, ActiveProfile()));
        var documentVersionId = Guid.NewGuid();
        // Service returns nothing — controller will NotFound. The point of
        // the test is that the Stub redirect was NOT taken; a NotFound is
        // proof that control reached the service.
        _consentService.GetConsentReviewDetailAsync(
                documentVersionId, userId, Arg.Any<CancellationToken>())
            .Returns((ConsentReviewDetail?)null);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Review(documentVersionId);

        Assert.IsType<NotFoundResult>(result);
        await _consentService.Received(1).GetConsentReviewDetailAsync(
            documentVersionId, userId, Arg.Any<CancellationToken>());
    }
}
