using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using Humans.Surveys.Contracts;
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

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyIdentityViewTests
{
    [HumansFact]
    public async Task ResultsAndInvitations_RenderCanonicalIdentity_AndEncodeNames()
    {
        using var culture = new CultureScope("en");
        var known = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(known, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = known, BurnerName = "Current <b>&" }, [], [], null));
        var at = Instant.FromUtc(2026, 1, 1, 12, 0);
        var scoped = new SurveyScopedResults(new SurveyResultsView(Guid.NewGuid(), "Results", SurveyStatus.Closed,
            2, 2, 1, new SurveyFunnel(2, 2, 0, 0), [], [
                new RespondentDetail(known, at, []),
                new RespondentDetail(missing, at, []),
            ]), 2, SurveyResultsScope.Combined);
        var results = SurveyResultsBuilder.Build(scoped);
        var send = new SurveySendViewModel
        {
            Id = results.SurveyId,
            Invitations = [
                new SurveyInviteStatus(known, "Stale known", null, false, false, at, null),
                new SurveyInviteStatus(missing, "Stale missing", null, false, false, at, null),
            ],
        };
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        foreach (var (page, model) in new (string, object)[] { ("Results", results), ("Send", send) })
        {
            var html = await RenderAsync(page, model, users);
            html.Should().Contain(HtmlEncoder.Default.Encode("Current <b>&"));
            html.Should().Contain(HtmlEncoder.Default.Encode(unknown));
            html.Should().NotContain("Stale known").And.NotContain("Stale missing").And.NotContain("Current <b>&");
        }
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task AnonymousAndSecretBallotResults_DoNotResolveRespondentIdentities(bool vote, bool embargoed)
    {
        var users = Substitute.For<IUserService>();
        var model = new SurveyResultsViewModel
        {
            Scope = vote ? SurveyResultsScope.Combined : SurveyResultsScope.Anonymous,
            IsAsociadoVote = vote,
            IsEmbargoed = embargoed,
            Respondents = [new SurveyResultsRespondentViewModel { UserId = Guid.NewGuid() }],
        };
        var html = await RenderAsync("Results", model, users);
        html.Should().NotContain("id=\"respondents\"");
        await users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SurveyIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(SurveysResource).Assembly)
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
            .GetView(null, $"/Views/SurveyAdmin/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "SurveyAdmin";
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
