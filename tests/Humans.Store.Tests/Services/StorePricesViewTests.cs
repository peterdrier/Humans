using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Store.Models;
using Humans.Store.Contracts;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
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
using NodaTime;
using NSubstitute;

namespace Humans.Store.Tests.Services;

public class StorePricesViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "10.5")]
    [Xunit.InlineData("es", "10,5")]
    [Xunit.InlineData("de", "10,5")]
    [Xunit.InlineData("it", "10,5")]
    [Xunit.InlineData("fr", "10,5")]
    [Xunit.InlineData("ca", "10,5")]
    public async Task VatBreakdowns_UseUiDecimalSeparator(string language, string vat)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var product = new ProductDto(Guid.NewGuid(), 2026, "Ice", "", 100, 10.5m, null,
            new LocalDate(2026, 7, 1), true);
        var index = await RenderAsync("Index", new IndexViewModel { Year = 2026, Catalog = [product] });

        var now = Instant.FromUtc(2026, 7, 1, 0, 0);
        var orderId = Guid.NewGuid();
        var line = new OrderLineDto(Guid.NewGuid(), orderId, product.Id, product.Name, 1,
            100, 10.5m, null, now, 100, 10.5m, 0, 110.5m);
        var order = new OrderDto(orderId, Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026,
            OrderState.Open, null, null, null, null, null, null, [line], [], 100, 10.5m, 0, 0, 110.5m, now);
        var detail = await RenderAsync("Order", new OrderViewModel { Order = order, CounterpartyDisplayName = "Camp" });
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        index.Should().Contain(HtmlEncoder.Default.Encode($" + {vat}% "));
        detail.Should().Contain(HtmlEncoder.Default.Encode($" + {vat}% "));
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(StorePricesViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddSingleton(Substitute.For<IAuditViewerService>());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StoreResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(AuditLogViewComponent).Assembly);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, $"/Views/Store/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Store";
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
