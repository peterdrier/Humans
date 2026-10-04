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
    [Xunit.InlineData("en", "10.5%", "20.5%")]
    [Xunit.InlineData("es", "10,5%", "20,5%")]
    [Xunit.InlineData("de", "10,5%", "20,5%")]
    [Xunit.InlineData("it", "10,5%", "20,5%")]
    [Xunit.InlineData("fr", "10,5%", "20,5%")]
    [Xunit.InlineData("ca", "10,5%", "20,5%")]
    public async Task BreakdownPercentages_UseUiCultureAndKeepChartNumbersInvariant(
        string language, string income, string expense)
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
        html.Should().Contain($">{income}</td>");
        html.Should().Contain($">{expense}</td>");
        html.Should().Contain("var incomeData = [10.5];");
        html.Should().Contain("var expenseData = [20.5];");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.InlineData("en", "1,234%")]
    [Xunit.InlineData("es", "1.234%")]
    [Xunit.InlineData("de", "1.234%")]
    [Xunit.InlineData("it", "1.234%")]
    [Xunit.InlineData("fr", "1\u202f234%")]
    [Xunit.InlineData("ca", "1.234%")]
    public async Task CategoryUtilization_UsesUiCultureAndKeepsProgressWidthNumeric(string language, string percentage)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var id = Guid.NewGuid();
        var model = new CoordinatorCategoryDetailViewModel
        {
            Category = new BudgetCategorySnapshot(id, Guid.NewGuid(), "Category", 100m,
                ExpenditureType.OpEx, null, 0, null,
                [new BudgetCategoryLineItemSnapshot(Guid.NewGuid(), id, "Line", 1234m,
                    null, null, null, null, 0, false, false, 0)]),
            Teams = [],
        };

        var html = await RenderAsync("CategoryDetail", model);

        html.Should().Contain($">{HtmlEncoder.Default.Encode(percentage)}</small>");
        html.Should().Contain("width: 100%");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BudgetSummaryViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
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
            .GetView(null, $"/Views/Budget/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Budget";
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
