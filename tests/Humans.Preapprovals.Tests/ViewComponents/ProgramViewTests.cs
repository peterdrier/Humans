using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Models;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using Humans.Preapprovals.Contracts;
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

namespace Humans.Preapprovals.Tests.ViewComponents;

public sealed class ProgramViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProgramAndForm_RenderManagerIdsInOrder_AndPreserveAttributionAndPostedIds(bool form)
    {
        using var culture = new CultureScope("en");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var issuer = Guid.NewGuid();
        var revoker = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        foreach (var (id, name) in new[] { (first, "First <script>&"), (second, "Second <b>&"),
                     (issuer, "Issuer <b>&"), (revoker, "Revoker <b>&") })
            users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(
                UserInfo.Create(new User { Id = id, BurnerName = name }, [], [], null));
        var now = Instant.FromUtc(2027, 3, 1, 12, 0);
        var programId = Guid.NewGuid();
        var grant = new GrantInfo(Guid.NewGuid(), programId, "Program", ProgramKind.FixedPerPerson,
            Guid.NewGuid(), null, 20m, GrantStatus.Open, null, null, issuer, now, null,
            new LocalDate(2027, 5, 1), now, now);
        var program = new ProgramInfo(programId, "Program", null, ProgramKind.FixedPerPerson,
            20m, null, 62900001, false, new LocalDate(2027, 5, 1), ProgramStatus.Open,
            [first, second, missing], [grant]);
        object model = form ? ProgramFormViewModel.From(program) : new AdminProgramViewModel
        {
            Program = program,
            Today = new LocalDate(2027, 3, 1),
            CanAdminister = true,
        };

        var html = await RenderAsync(form ? "Form" : "Program", model, users);
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking));
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        foreach (var name in new[] { "First <script>&", "Second <b>&", unknown })
            html.Should().Contain(HtmlEncoder.Default.Encode(name));
        html.Should().NotContain("First <script>&").And.NotContain("Second <b>&");
        if (form)
        {
            foreach (var id in new[] { first, second, missing })
                html.Should().Contain($"name=\"ManagerUserIds\" value=\"{id}\"");
        }
        else
        {
            text.Should().Contain($"First <script>&, Second <b>&, {unknown}");
            html.Should().Contain(HtmlEncoder.Default.Encode("Issuer <b>&"));

        }
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProgramViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(PreapprovalsResource).Assembly)
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
            .GetView(null, $"/Views/PreapprovalsAdmin/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "PreapprovalsAdmin";
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
