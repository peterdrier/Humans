using AwesomeAssertions;
using System.Text.Encodings.Web;
using Humans.Base.ViewComponents;
using Humans.Base;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Domain;
using Humans.Users.Services;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.ViewComponents;

public class HumanViewComponentTests
{
    [HumansTheory]
    [Xunit.InlineData("", "?")]
    [Xunit.InlineData("Alice", "A")]
    [Xunit.InlineData("🐝 Bee", "🐝")]
    [Xunit.InlineData("A\u0301da", "A\u0301")]
    [Xunit.InlineData("👩‍🚒 Fire", "👩‍🚒")]
    [Xunit.InlineData("👌🏽 Human", "👌🏽")]
    [Xunit.InlineData("🇪🇸 Human", "🇪🇸")]
    [Xunit.InlineData("<Admin", "<")]
    public async Task Fallback_avatar_renders_a_complete_first_text_element(string name, string initial)
    {
        var item = new RazorCompiledItemLoader().LoadItems(typeof(HumanViewComponent).Assembly)
            .Single(i => string.Equals(i.Identifier, "/Views/Shared/Components/Human/Default.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<HumanViewModel>)Activator.CreateInstance(item.Type)!;
        var html = Substitute.For<IHtmlHelper<HumanViewModel>>();
        html.Raw(Arg.Any<string>()).Returns(call => new HtmlString(call.Arg<string>()));
        item.Type.GetProperty("Html")!.SetValue(page, html);
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<HumanViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = new HumanViewModel { DisplayName = name, Layout = HumanLayout.Avatar, Size = 32 },
        };
        page.ViewContext = new ViewContext { Writer = writer, ViewData = data };
        page.ViewData = data;

        await page.ExecuteAsync();

        writer.ToString().Should().Contain($">{HtmlEncoder.Default.Encode(initial)}</div>");
    }

    /// <summary>
    /// Base names Users' controllers by string; a stale name makes <c>Url.Action</c> return null and every avatar
    /// silently degrades to its initial.
    /// </summary>
    [HumansFact]
    public async Task Custom_picture_and_profile_link_resolve_against_real_routes()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var info = UserInfoFactory.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Test",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            externalLogins: [],
            profile: new Profile
            {
                Id = profileId,
                UserId = userId,
                BurnerName = "Sparky",
                ProfilePictureContentType = "image/jpeg",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: []);

        var userService = Substitute.For<IUserService>();
        userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);

        await using var app = BuildUsersRoutedApp();
        var httpContext = new DefaultHttpContext { RequestServices = app.Services };
        httpContext.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "test"));

        var sut = new HumanViewComponent(userService, app.Services.GetRequiredService<IUrlHelperFactory>(),
            app.Services.GetRequiredService<IStringLocalizer<SharedResource>>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext, RouteData = new RouteData() },
            },
        };

        var result = await sut.InvokeAsync(userId, HumanLayout.Avatar) as ViewViewComponentResult;
        var model = (HumanViewModel)result!.ViewData!.Model!;

        model.ProfilePictureUrl.Should().StartWith($"/Profile/Picture?id={profileId}");
        model.Href.Should().Be($"/Profile/{userId}");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Identity_reads_resolve_by_default_and_preserve_raw_merge_accounts_when_requested(bool raw)
    {
        var id = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = Guid.NewGuid(), BurnerName = "Survivor" }, [], [], null));
        users.GetRawUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = id, BurnerName = "Archived account" }, [], [], null));
        var component = new HumanViewComponent(users, Substitute.For<IUrlHelperFactory>(),
            Substitute.For<IStringLocalizer<SharedResource>>())
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() } }
        };

        var result = raw
            ? await component.InvokeAsync(id, link: HumanLink.None, raw: true)
            : await component.InvokeAsync(id, link: HumanLink.None);
        var model = (HumanViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.DisplayName.Should().Be(raw ? "Archived account" : "Survivor");
        if (raw)
            await users.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        else
            await users.DidNotReceive().GetRawUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Identity_reads_preserve_request_cancellation(bool raw)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        users.GetRawUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new HumanViewComponent(users, Substitute.For<IUrlHelperFactory>(),
            Substitute.For<IStringLocalizer<SharedResource>>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid(), raw: raw);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WebApplication BuildUsersRoutedApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(ProfileViewController).Assembly)
            .ConfigureApplicationPartManager(apm =>
                apm.FeatureProviders.Add(new SectionControllerFeatureProvider()));
        var app = builder.Build();
        // UseEndpoints is what hands the mapped routes to LinkGenerator; the host is never started.
        app.UseRouting();
        app.MapControllers();
        app.UseEndpoints(_ => { });
        return app;
    }
}
