using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Cantina.Controllers;
using Humans.Cantina.Domain;
using Humans.Cantina.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Cantina.Tests.Models;

public sealed class CantinaDailyViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Day_RendersCurrentHumansWithoutChangingHealthDisclosure(bool revealed)
    {
        using var culture = new CultureScope("en");
        var userId = Guid.NewGuid();
        const string name = "Current <script>&";
        const string allergy = "Private allergy note";
        const string intolerance = "Private intolerance note";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = userId, BurnerName = name }, [], [],
                UserFixtures.Profile()));
        var matrix = new DailyMatrixDto(0, new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 7), "Event", 0, 2, 1,
        [
            new(userId, "Stored known name", "Vegan", new HashSet<string>(StringComparer.Ordinal) { DietaryOptions.OtherOption }, allergy,
                new HashSet<string>(StringComparer.Ordinal) { DietaryOptions.OtherOption }, intolerance),
            new(Guid.NewGuid(), "Stored missing name", null, new HashSet<string>(StringComparer.Ordinal), null, new HashSet<string>(StringComparer.Ordinal), null),
        ]);

        var html = await RenderAsync(matrix, users, revealed);

        html.Should().Contain(HtmlEncoder.Default.Encode(name)).And.NotContain(name)
            .And.NotContain("Stored known name").And.NotContain("Stored missing name");
        html.Should().Contain(HtmlEncoder.Default.Encode(new ResourceManager(typeof(SharedResource))
            .GetString("Common_Unknown", CultureInfo.CurrentUICulture)!));
        html.Contains(allergy, StringComparison.Ordinal).Should().Be(revealed);
        html.Contains(intolerance, StringComparison.Ordinal).Should().Be(revealed);
    }

    private static async Task<string> RenderAsync(DailyMatrixDto model, IUserService users, bool revealed)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CantinaDailyViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(CantinaResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        builder.Services.AddSingleton(users);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Cantina/Day.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Cantina";
        route.Values["action"] = "Day";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
            [CantinaController.HealthRevealedKey] = revealed,
        };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
