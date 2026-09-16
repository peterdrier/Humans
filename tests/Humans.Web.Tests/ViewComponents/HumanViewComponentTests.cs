using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.ViewComponents;

public class HumanViewComponentTests
{
    [HumansFact]
    public async Task Custom_picture_resolves_through_the_registered_profile_route()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            foreach (var assembly in SectionDiscoveryExtensions.ActiveSectionAssemblies())
                manager.ApplicationParts.Add(new AssemblyPart(assembly));
            manager.FeatureProviders.Clear();
            manager.FeatureProviders.Add(new SectionControllerFeatureProvider());
        });
        await using var app = builder.Build();
        // Register endpoint data sources without starting a listener.
        var pipeline = new ApplicationBuilder(app.Services);
        pipeline.UseRouting();
        pipeline.UseEndpoints(endpoints => endpoints.MapControllers());

        var profile = UserFixtures.Profile(burnerName: "Picture Human") with { HasCustomPicture = true };
        var info = UserInfo.Create(new User { Id = Guid.NewGuid(), Email = "picture@example.com" },
            [], [], [], profile, []);
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(info.Id, Arg.Any<CancellationToken>()).Returns(info);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        // A routed request chooses EndpointRoutingUrlHelper, exercising real MVC link generation.
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "test"));
        var component = new HumanViewComponent(users, app.Services.GetRequiredService<IUrlHelperFactory>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = http, RouteData = new RouteData() },
            },
        };

        var result = Assert.IsType<ViewViewComponentResult>(await component.InvokeAsync(info.Id));
        var model = Assert.IsType<HumanViewModel>(result.ViewData!.Model);

        Assert.Equal(info.ProfilePictureUrl, model.ProfilePictureUrl);
        Assert.False(string.IsNullOrWhiteSpace(model.ProfilePictureUrl));
    }
}
