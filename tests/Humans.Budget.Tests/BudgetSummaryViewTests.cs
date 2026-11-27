using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Primitives;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Budget.Models;
using Humans.Budget.Contracts;
using System.Globalization;
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

namespace Humans.Budget.Tests;

public class BudgetSummaryViewTests
{
    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task BreakdownPercentages_UiCulture_FormatsText(string language)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var html = await RenderAsync("Summary", new BudgetSummaryViewModel
        {
            YearName = "2026",
            TotalIncome = 100,
            TotalExpenses = -100,
            IncomeSlices = [new BudgetSliceResult { Name = "Income", Amount = 10.5m, Percentage = 10.5m }],
            ExpenseSlices = [new BudgetSliceResult { Name = "Expense", Amount = 20.5m, Percentage = 20.5m }],
        });

        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var ui = CultureInfo.GetCultureInfo(language);
        html.Should().Contain($">{10.5m.ToString("N1", ui)}%</td>");
        html.Should().Contain($">{20.5m.ToString("N1", ui)}%</td>");
        html.Should().Contain("var incomeData = [10.5];");
        html.Should().Contain("var expenseData = [20.5];");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task CategoryUtilization_UiCulture_FormatsText(string language)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var id = Guid.NewGuid();
        var model = new CategoryDetailViewModel
        {
            Category = new BudgetCategorySnapshot(id, Guid.NewGuid(), "Category", 100m,
                ExpenditureType.OpEx, null, 0, null,
                [new BudgetCategoryLineItemSnapshot(Guid.NewGuid(), "Line", 1234m,
                    null, null, null, null, 0, false, false, 0)]),
            Teams = [],
        };

        var html = await RenderAsync("CategoryDetail", model);

        html.Should().Contain($">{HtmlEncoder.Default.Encode(1234m.ToString("N0", CultureInfo.GetCultureInfo(language)))}%</small>");
        html.Should().Contain("width: 100%");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task DecimalForms_PostedValues_PreserveAmounts(string language)
    {
        using var culture = new CultureScope(language);
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var yearId = Guid.NewGuid();
        var category = new BudgetCategorySnapshot(Guid.NewGuid(), Guid.NewGuid(), "Category", 12.34m,
            ExpenditureType.OpEx, null, 0, null,
            [new BudgetCategoryLineItemSnapshot(Guid.NewGuid(), "Line", -12.34m, null, null, null, null, 0, false, false, 0)]);
        var year = new BudgetYearDetail(yearId, "2027", "Budget", BudgetYearStatus.Draft, false,
        [
            new BudgetGroupDetail(Guid.NewGuid(), yearId, "Group", 0, false, false, false, null, []),
            new BudgetGroupDetail(Guid.NewGuid(), yearId, "Tickets", 1, false, false, true,
                new TicketingProjectionSnapshot(null, null, 0, 12.34m, 12.34m, 0, 12.34m, 12.34m, 12.34m), []),
        ]);
        var pages = new (string Controller, string Page, object Model)[]
        {
            ("Budget", "CategoryDetail", new CategoryDetailViewModel { Category = category, CanEdit = true, Teams = [] }),
            ("BudgetAdmin", "CategoryDetail", category),
            ("BudgetAdmin", "YearDetail", new FinanceOverviewViewModel
            {
                Year = year, AllYears = [], IncomeSlices = [], ExpenseSlices = [],
            }),
        };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var tested = 0;
        foreach (var (controller, page, model) in pages)
        {
            var html = await RenderAsync(page, model, controller);
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
                foreach (var input in inputs.Where(i => i.GetValueOrDefault("name") is "amount" or "allocatedAmount"
                    or "dailySalesRate" or "averageTicketPrice" or "stripeFeePercent" or "stripeFeeFixed" or "platformFeePercent"))
                {
                    var name = input["name"];
                    var value = input["value"];
                    var expected = name is "amount" && value.Length > 1 ? -12.34m : 12.34m;
                    if (!string.Equals(value, "0", StringComparison.Ordinal))
                        value.Should().Be(expected.ToString(CultureInfo.InvariantCulture), $"{controller}/{page}/{name} must render an HTML number in {language}");
                    fields[name] = string.Equals(value, "0", StringComparison.Ordinal) ? "12.34" : value;
                    var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(decimal));
                    var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
                    var action = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
                    var binding = DefaultModelBindingContext.CreateBindingContext(action,
                        new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.CurrentCulture),
                        metadata, bindingInfo: null, modelName: name);
                    await binder.BindModelAsync(binding);
                    binding.Result.IsModelSet.Should().BeTrue();
                    binding.Result.Model.Should().Be(expected, $"{controller}/{page}/{name} must preserve the posted amount in {language}");
                    tested++;
                }
            }
        }
        tested.Should().Be(11);
    }

    private static async Task<string> RenderAsync(string page, object model, string controller = "Budget")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BudgetSummaryViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        builder.Services.AddSingleton(authorization);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(BudgetResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
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
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var route = new RouteData();
        route.Values["controller"] = controller;
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        return writer.ToString();
    }
}
