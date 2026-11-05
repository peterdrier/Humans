using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Governance.Models;
using Humans.Governance.Domain;
using Humans.Governance.Data;
using Humans.Governance.Services.Dtos;
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

namespace Humans.Governance.Tests.Models;

public sealed class AssemblyBallotIdentityViewTests
{
    [HumansFact]
    public async Task DisclosedBallots_RenderCanonicalIdentity_AndKeepErasedVotersUnlinked()
    {
        using var culture = new CultureScope("en");
        var known = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(known, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = known, BurnerName = "Current <b>&" }, [], [], null));
        var at = Instant.FromUtc(2026, 1, 1, 12, 0);
        AssemblyBallotDisclosureRow[] ballots = [
            new(missing, "B stale", false, AssemblyBallotChoice.No, null, 2),
            new(null, "Erased identity", false, AssemblyBallotChoice.Abstain, null, 3),
            new(known, "A stale", true, AssemblyBallotChoice.Yes, null, 1),
        ];
        var vote = new AssemblyVoteDetail(Guid.NewGuid(), "Motion", "Official text", "es", false, null,
            AssemblyVoteKind.YesNo, RequiredMajority.Simple, IndicativeAudience.None,
            BallotDisclosure.RosterSeesNames, AssemblyVoteStatus.Closed, at, at, null, null,
            true, true, false, [], null, new AssemblyVoteParticipation(1, 2, 1, 2, 0, 0, null));
        var result = new AssemblyVoteResult(
            new AssemblyVoteAudienceResult(1, 1, new YesNoTally(1, 0, 0), null, null, AssemblyVoteVerdict.Passed, []),
            null, AssemblyVoteMethod.YesNo, at);
        var results = new AssemblyVoteResultsViewModel
        {
            Results = new AssemblyVoteResultsView(vote, result, [], ballots, "Recorded acta"),
        };
        var admin = new AssemblyVoteBallotsViewModel { VoteId = vote.Id, Ballots = ballots };
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        foreach (var (page, model) in new (string, object)[] { ("Results", results), ("Admin/Ballots", admin) })
        {
            var html = await RenderAsync(page, model, users);
            var encoded = HtmlEncoder.Default.Encode("Current <b>&");
            html.Should().Contain(encoded).And.Contain(HtmlEncoder.Default.Encode(unknown));
            html.Should().NotContain("Current <b>&").And.NotContain("A stale").And.NotContain("B stale").And.NotContain("Erased identity");
            html.Should().MatchRegex(@"<td>\s*—\s*</td>");
            html.IndexOf(encoded, StringComparison.Ordinal).Should().BeLessThan(html.IndexOf(HtmlEncoder.Default.Encode(unknown), StringComparison.Ordinal));
        }
        await users.DidNotReceive().GetUserInfoAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AssemblyBallotIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(GovernanceResource).Assembly)
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
            .GetView(null, $"/Views/Governance/Votes/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "AssemblyVotes";
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
