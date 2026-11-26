using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
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
using Microsoft.Extensions.Primitives;
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
        var grant = new GrantInfo(Guid.NewGuid(), programId, "Program",
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
            CanManage = true,
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

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task AmountForms_PostedValues_PreserveAmount(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        using var assertions = new AssertionScope();
        var now = Instant.FromUtc(2027, 3, 1, 12, 0);
        var programId = Guid.NewGuid();
        var application = new GrantInfo(Guid.NewGuid(), programId, "Program", Guid.NewGuid(), null,
            12.34m, GrantStatus.Applied, null, 12.34m, null, null, null,
            new LocalDate(2027, 5, 1), now, now);
        var grant = application with { Id = Guid.NewGuid(), Status = GrantStatus.Open };
        var program = new ProgramInfo(programId, "Program", null, ProgramKind.PerGrantAmount,
            null, 12.34m, 62900001, true, new LocalDate(2027, 5, 1), ProgramStatus.Open, [], [application, grant]);
        var users = Substitute.For<IUserService>();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var pages = new (string Page, object Model, string Controller)[]
        {
            ("Program", new AdminProgramViewModel
            {
                Program = program, Today = new LocalDate(2027, 3, 1), CanAdminister = true, CanManage = true,
            }, "PreapprovalsAdmin"),
            ("Form", ProgramFormViewModel.From(program with { PerPersonAmount = 12.34m }), "PreapprovalsAdmin"),
            ("Program", new MemberProgramViewModel
            {
                Program = program, Today = new LocalDate(2027, 3, 1), MyGrants = [application], Form = new(),
            }, "Preapprovals"),
        };
        var tested = 0;
        foreach (var (page, model, controller) in pages)
        {
            var html = await RenderAsync(page, model, users, controller);
            foreach (Match form in Regex.Matches(html, "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                var inputs = Regex.Matches(form.Value, "<input\\b[^>]*>", RegexOptions.NonBacktracking)
                    .Cast<Match>().Select(input => Regex.Matches(input.Value, "(?<name>[\\w-]+)=\"(?<value>[^\"]*)\"", RegexOptions.NonBacktracking)
                        .Cast<Match>().ToDictionary(a => a.Groups["name"].Value, a => WebUtility.HtmlDecode(a.Groups["value"].Value), StringComparer.Ordinal))
                    .ToList();
                var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
                foreach (var input in inputs.Where(i => i.ContainsKey("name")))
                {
                    var name = input["name"];
                    fields[name] = StringValues.Concat(fields.GetValueOrDefault(name), input.GetValueOrDefault("value", ""));
                }
                foreach (var input in inputs.Where(i => string.Equals(i.GetValueOrDefault("type"), "number", StringComparison.Ordinal)))
                {
                    var name = input["name"];
                    var value = input.GetValueOrDefault("value", "");
                    if (value.Length > 0)
                        value.Should().Be("12.34", $"{controller}/{page} must render a valid HTML number in {cultureName}");
                    fields[name] = value.Length == 0 ? "12.34" : value;
                    var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(decimal?));
                    var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
                    var action = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
                    var binding = DefaultModelBindingContext.CreateBindingContext(action,
                        new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.CurrentCulture),
                        metadata, bindingInfo: null, modelName: name);
                    await binder.BindModelAsync(binding);
                    binding.Result.IsModelSet.Should().BeTrue();
                    binding.Result.Model.Should().Be(12.34m, $"{controller}/{page} must preserve the posted amount in {cultureName}");
                    tested++;
                }
            }
        }
        tested.Should().Be(6);
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users, string controller = "PreapprovalsAdmin")
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
            .GetView(null, $"/Views/{controller}/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = controller;
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
