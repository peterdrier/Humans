using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Domain;
using Humans.Users.Services;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.ViewComponents;

public class HumanViewComponentTests
{
    /// <summary>
    /// <see cref="HumanViewComponent"/> lives in Base and names Users' controllers by string, so a
    /// controller split leaves it pointing at a route that no longer exists. <c>Url.Action</c> then
    /// returns null and every avatar silently degrades to its initial — no exception, no log
    /// (peterdrier/Humans#1603 moved <c>Picture</c> to <c>ProfileViewController</c>). Resolved
    /// against the real Users routes so a stale name fails here.
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
            eventParticipations: [],
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
            volunteerHistory: [],
            communicationPreferences: []);

        var userService = Substitute.For<IUserServiceRead>();
        userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);

        await using var app = BuildUsersRoutedApp();
        var httpContext = new DefaultHttpContext { RequestServices = app.Services };
        httpContext.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "test"));

        var sut = new HumanViewComponent(userService, app.Services.GetRequiredService<IUrlHelperFactory>())
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

    private static WebApplication BuildUsersRoutedApp()
    {
        var builder = WebApplication.CreateBuilder();
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
