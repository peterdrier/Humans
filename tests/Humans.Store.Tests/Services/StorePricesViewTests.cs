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

    [HumansTheory]
    [Xunit.InlineData("en", OrderState.Open, "Open")]
    [Xunit.InlineData("es", OrderState.Open, "Abierto")]
    [Xunit.InlineData("de", OrderState.Open, "Offen")]
    [Xunit.InlineData("it", OrderState.Open, "Aperto")]
    [Xunit.InlineData("fr", OrderState.Open, "Ouverte")]
    [Xunit.InlineData("ca", OrderState.Open, "Oberta")]
    [Xunit.InlineData("en", OrderState.InvoiceIssued, "Invoice issued")]
    [Xunit.InlineData("es", OrderState.InvoiceIssued, "Factura emitida")]
    public async Task MemberOrderList_UsesExistingStateLabels(string language, OrderState state, string label)
    {
        using var culture = new CultureScope(language);
        var campId = Guid.NewGuid();
        var order = new OrderDto(Guid.NewGuid(), campId, null, OrderCounterpartyType.Camp, "Camp", 2026,
            state, null, null, null, null, null, null, [], [], 0, 0, 0, 0, 0, Instant.FromUtc(2026, 7, 1, 0, 0));
        var html = await RenderAsync("Index", new IndexViewModel
        {
            Year = 2026,
            Counterparties = [new CounterpartyOrders(OrderCounterpartyType.Camp, campId, "Camp", 2026, [order])],
            CanManageByCounterparty = new Dictionary<Guid, bool> { [campId] = false },
        });

        html.Should().Contain($"class=\"badge bg-secondary\">{HtmlEncoder.Default.Encode(label)}</span>");
    }

    [HumansTheory]
    [Xunit.InlineData("en", "Date", "Action", "Description")]
    [Xunit.InlineData("es", "Fecha", "Acción", "Descripción")]
    [Xunit.InlineData("de", "Datum", "Aktion", "Beschreibung")]
    [Xunit.InlineData("it", "Data", "Azione", "Descrizione")]
    [Xunit.InlineData("fr", "Date", "Action", "Description")]
    [Xunit.InlineData("ca", "Data", "Acció", "Descripció")]
    public async Task PriceHistory_UsesHostColumnLabels(string language, string date, string action, string description)
    {
        using var culture = new CultureScope(language);
        var now = Instant.FromUtc(2026, 7, 1, 0, 0);
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var line = new OrderLineDto(Guid.NewGuid(), orderId, productId, "Ice", 1,
            100, 0, null, now, 100, 0, 0, 100);
        var order = new OrderDto(orderId, Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026,
            OrderState.Open, null, null, null, null, null, null, [line], [], 100, 0, 0, 0, 100, now);
        var history = new AuditEvent(Guid.NewGuid(), now, AuditAction.StoreProductPriceChanged,
            null, null, "Product", productId, null, null, null, null, null, null, null, "Price history fixture");

        var html = await RenderAsync("Order", new OrderViewModel { Order = order, CounterpartyDisplayName = "Camp" }, [history]);

        var table = html[html.LastIndexOf("<thead>", StringComparison.Ordinal)..];
        var headers = table[..table.IndexOf("</thead>", StringComparison.Ordinal)];
        foreach (var label in new[] { date, action, description })
            headers.Should().Contain(HtmlEncoder.Default.Encode(label));
    }

    [HumansTheory]
    [Xunit.InlineData("en", "2,469.00", "1,234.50")]
    [Xunit.InlineData("es", "2.469,00", "1.234,50")]
    [Xunit.InlineData("de", "2.469,00", "1.234,50")]
    [Xunit.InlineData("it", "2.469,00", "1.234,50")]
    [Xunit.InlineData("fr", "2\u202f469,00", "1\u202f234,50")]
    [Xunit.InlineData("ca", "2.469,00", "1.234,50")]
    public async Task AdminSummary_CampTotalsUseUiCultureLikeRows(string language, string due, string paidAndBalance)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var model = new SummaryViewModel
        {
            Summary = new SummaryDto(2026,
                [new OrderSummaryDto(Guid.NewGuid(), OrderCounterpartyType.Camp, Guid.NewGuid(), "Camp",
                    OrderState.Open, 2469m, 1234.5m, 1234.5m)], [], new CrossTabDto([], [])),
        };

        var html = await RenderAsync("Summary", model, viewPath: "/Views/StoreAdmin/Summary.cshtml");

        var footer = html[html.IndexOf("<tfoot", StringComparison.Ordinal)..];
        footer = footer[..footer.IndexOf("</tfoot>", StringComparison.Ordinal)];
        footer.Should().Contain(HtmlEncoder.Default.Encode(due));
        footer.Split(HtmlEncoder.Default.Encode(paidAndBalance), StringSplitOptions.None).Should().HaveCount(3);
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.InlineData((int)StripeReconciliationStatus.RecordedFailed, false, false)]
    [Xunit.InlineData((int)StripeReconciliationStatus.RecordedPending, false, false)]
    [Xunit.InlineData((int)StripeReconciliationStatus.Unmatched, false, false)]
    [Xunit.InlineData((int)StripeReconciliationStatus.Recorded, true, false)]
    [Xunit.InlineData((int)StripeReconciliationStatus.Recorded, false, true)]
    public async Task Reconciliation_DoesNotClaimSuccessWhileDiscrepanciesRemain(
        int status, bool orphan, bool expectedSuccess)
    {
        var now = Instant.FromUtc(2026, 6, 4, 12, 0);
        var row = new StripeReconciliationRow("cs", "pi", 100m, "paid", now, Guid.NewGuid(), "Camp", (StripeReconciliationStatus)status);
        var report = new StripeReconciliationReport(true, true, true, [row],
            orphan ? [new StripeOrphanPayment("pi_orphan", Guid.NewGuid(), "Camp", 100m, now)] : []);
        var model = new PaymentsReconciliationViewModel { Report = report, Rows = [] };

        var html = await RenderAsync("Payments", model, viewPath: "/Views/StoreAdmin/Payments.cshtml");

        html.Contains("Stripe and the ledger are reconciled.", StringComparison.Ordinal).Should().Be(expectedSuccess);
    }

    private static async Task<string> RenderAsync(string page, object model, IReadOnlyList<AuditEvent>? history = null, string? viewPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(StorePricesViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        var audit = Substitute.For<IAuditViewerService>();
        audit.GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(history ?? []);
        builder.Services.AddSingleton(audit);
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
            .GetView(null, viewPath ?? $"/Views/Store/{page}.cshtml", isMainPage: false);
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
