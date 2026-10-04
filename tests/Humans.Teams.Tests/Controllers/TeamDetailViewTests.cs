using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Enums;
using Humans.Base.ViewComponents;
using Humans.Teams.Models;
using Microsoft.AspNetCore.Authorization;
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
using NSubstitute;

namespace Humans.Teams.Tests.Controllers;

public class TeamDetailViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en", SystemTeamType.Volunteers, "Volunteers")]
    [Xunit.InlineData("es", SystemTeamType.Volunteers, "Voluntarios")]
    [Xunit.InlineData("de", SystemTeamType.Volunteers, "Freiwillige")]
    [Xunit.InlineData("it", SystemTeamType.Volunteers, "Volontari")]
    [Xunit.InlineData("fr", SystemTeamType.Volunteers, "Bénévoles")]
    [Xunit.InlineData("ca", SystemTeamType.Volunteers, "Voluntaris")]
    [Xunit.InlineData("es", SystemTeamType.BarrioLeads, "Responsables de campamento")]
    public async Task SystemTeamType_UsesLocalizedLabelInBadgeAndMembershipCriteria(
        string culture, SystemTeamType type, string expected)
    {
        using var language = new CultureScope(culture);
        var model = new TeamDetailViewModel
        {
            Name = "Example", Slug = "example", IsAuthenticated = true,
            IsSystemTeam = true, SystemTeamType = type,
        };
        var html = await RenderAsync("/Views/Team/Details.cshtml", model);

        html.Split(HtmlEncoder.Default.Encode(expected), StringSplitOptions.None)
            .Should().HaveCount(3, "the badge and membership criteria both use the translated team type");
    }

    [HumansTheory]
    [Xunit.InlineData(false, 150, false)]
    [Xunit.InlineData(false, 150, true)]
    [Xunit.InlineData(true, 200, false)]
    [Xunit.InlineData(true, 200, true)]
    public async Task DescriptionPreview_PreservesWholeSurrogatePairs(bool directory, int limit, bool fits)
    {
        var prefix = new string('a', limit - (fits ? 2 : 1));
        var team = new TeamSummaryViewModel
        {
            Name = "Example", Slug = "example", Description = prefix + "😀tail",
        };
        var model = directory ? (object)new TeamIndexViewModel { Departments = [team] } : team;
        var path = directory ? "/Views/Team/Index.cshtml" : "/Views/Team/_TeamCard.cshtml";

        var html = await RenderAsync(path, model);

        html.Should().Contain(SanitizedMarkdownRenderer.Render(prefix + (fits ? "😀" : "") + "..."));
    }

    private static async Task<string> RenderAsync(string path, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TeamDetailViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(TeamsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Team";
        route.Values["action"] = "Details";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        return writer.ToString();
    }
}
