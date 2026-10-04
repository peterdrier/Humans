using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Expenses.Contracts;
using Humans.Expenses.Models;
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

namespace Humans.Expenses.Tests.Models;

public class ExpenseReportViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false, 60, false)]
    [Xunit.InlineData(false, 60, true)]
    [Xunit.InlineData(true, 80, false)]
    [Xunit.InlineData(true, 80, true)]
    public async Task SubjectPreview_PreservesWholeSurrogatePairs(bool review, int limit, bool fits)
    {
        var prefix = new string('a', limit - (fits ? 2 : 1));
        var report = new ExpenseReportDto
        {
            Id = Guid.NewGuid(), SubmitterUserId = Guid.NewGuid(), BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(), Status = ExpenseReportStatus.Draft,
            PayeeName = "", PayeeIban = "", Total = 0, Lines = [],
            CreatedAt = Instant.FromUtc(2026, 7, 1, 0, 0), UpdatedAt = Instant.FromUtc(2026, 7, 1, 0, 0),
            Note = prefix + "😀tail",
        };
        object model = review ? new ExpenseReviewViewModel
        {
            Reports = [report], SubmitterNames = new Dictionary<Guid, string>(),
            DepartmentNames = new Dictionary<Guid, string>(), FailedHoldedPushReportIds = new HashSet<Guid>(),
        } : new ExpensesIndexViewModel { Reports = [report] };

        var html = await RenderAsync(review ? "Review" : "Index", model);

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "…"));
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ExpenseReportViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(ExpensesResource).Assembly)
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
            .GetView(null, $"/Views/Expenses/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Expenses";
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
