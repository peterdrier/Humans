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
    [HumansFact]
    public async Task VatBreakdowns_UseUiDecimalSeparator()
    {
        const string vat = "10,5";
        using var inputCulture = new CultureScope(CultureCatalog.DefaultCultureCode);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CultureCatalog.CanonicalLegalCultureCode);
        var product = new ProductDto(Guid.NewGuid(), 2026, "Ice", "", 100, 10.5m, null,
            new LocalDate(2026, 7, 1), true);
        var index = await RenderAsync("Index", new IndexViewModel { Year = 2026, Catalog = [product] });

        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        index.Should().Contain(HtmlEncoder.Default.Encode($" + {vat}% "));
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansFact]
    public async Task OrderPayments_ShowMoneyBackAsReturnedOrRefunded_AndAddUpToTheOrder()
    {
        using var culture = new CultureScope(CultureCatalog.DefaultCultureCode);
        var now = Instant.FromUtc(2026, 7, 1, 0, 0);
        OrderPaymentDto PaymentDto(decimal amount, PaymentMethod method) =>
            new(Guid.NewGuid(), amount, method, PaymentStatus.Paid, null, "ref", now, null);
        var order = new OrderDto(Guid.NewGuid(), Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026,
            OrderState.Open, null, null, null, null, null, null, [],
            [PaymentDto(130m, PaymentMethod.BankTransfer), PaymentDto(20m, PaymentMethod.DepositReturn), PaymentDto(-30m, PaymentMethod.Refund)],
            100m, 21m, 20m, 120m, 21m, now)
        { TotalDueEur = 141m };

        var html = await RenderAsync("Order", new OrderViewModel { Order = order });

        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        html.Should().Contain(">Returned</span>").And.Contain(">Refunded</span>");
        html.Should().Contain(HtmlEncoder.Default.Encode("Order total (lines, VAT and deposits)"));
        html.Should().Contain(HtmlEncoder.Default.Encode(141m.ToEuro())).And.Contain(HtmlEncoder.Default.Encode(21m.ToEuro()));
    }

    [HumansTheory]
    [Xunit.InlineData(OrderState.Open, "Open")]
    [Xunit.InlineData(OrderState.InvoiceIssued, "Invoice issued")]
    public async Task MemberOrderList_UsesExistingStateLabels(OrderState state, string label)
    {
        using var culture = new CultureScope(CultureCatalog.DefaultCultureCode);
        var campId = Guid.NewGuid();
        var order = new OrderDto(Guid.NewGuid(), campId, null, OrderCounterpartyType.Camp, "Camp", 2026,
            state, null, null, null, null, null, null, [], [], 0, 0, 0, 0, 0, Instant.FromUtc(2026, 7, 1, 0, 0));
        var html = await RenderAsync("Index", new IndexViewModel
        {
            Year = 2026,
            Counterparties = [new CounterpartyOrders(OrderCounterpartyType.Camp, campId, "Camp", [order])],
            CanManageByCounterparty = new Dictionary<Guid, bool> { [campId] = false },
        });

        html.Should().Contain($"class=\"badge bg-secondary\">{HtmlEncoder.Default.Encode(label)}</span>");
    }

    [HumansFact]
    public async Task OrderHistory_UsesHostColumnLabels()
    {
        using var culture = new CultureScope(CultureCatalog.DefaultCultureCode);
        var now = Instant.FromUtc(2026, 7, 1, 0, 0);
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var line = new OrderLineDto(Guid.NewGuid(), productId, "Ice", 1,
            100, 0, null, 0, 100);
        var order = new OrderDto(orderId, Guid.NewGuid(), null, OrderCounterpartyType.Camp, "Camp", 2026,
            OrderState.Open, null, null, null, null, null, null, [line], [], 100, 0, 0, 0, 100, now);
        var history = new AuditEvent(Guid.NewGuid(), now, new AuditActionDefinition(StoreAuditActions.StoreProductPriceChanged.Name),
            null, null, "Product", productId, null, null, null, null, null, null, null, "Price history fixture");

        var html = await RenderAsync("Order", new OrderViewModel { Order = order }, [history]);

        var table = html[html.LastIndexOf("<thead>", StringComparison.Ordinal)..];
        var headers = table[..table.IndexOf("</thead>", StringComparison.Ordinal)];
        foreach (var label in new[] { "Date", "Who", "Action", "Description" })
            headers.Should().Contain(HtmlEncoder.Default.Encode(label));
        html.Should().Contain("Order history").And.Contain("Price history fixture");
        // A lead's view never asks for the admin-only entries.
        await _lastAudit!.Received().GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Is<IReadOnlyList<string>?>(a => a != null && a.Contains(StoreAuditActions.StoreLineRemoved.Name)
                && !a.Contains(StoreAuditActions.StorePaymentDeleted.Name)),
            Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdminSummary_CampTotalsUseUiCultureLikeRows()
    {
        const string due = "2.469,00";
        const string paidAndBalance = "1.234,50";
        using var inputCulture = new CultureScope(CultureCatalog.DefaultCultureCode);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CultureCatalog.CanonicalLegalCultureCode);
        var model = new SummaryViewModel
        {
            Summary = new SummaryDto(2026,
                [new OrderSummaryDto(Guid.NewGuid(), OrderCounterpartyType.Camp, "Camp",
                    OrderState.Open, 2469m, 1234.5m, 1234.5m),
                 new OrderSummaryDto(Guid.NewGuid(), OrderCounterpartyType.Team, "Team",
                    OrderState.Open, 9000m, 0m, 0m)], [], new CrossTabDto([], [])),
        };

        var html = await RenderAsync("Summary", model, viewPath: "/Views/StoreAdmin/Summary.cshtml");

        var footer = html[html.IndexOf("<tfoot", StringComparison.Ordinal)..];
        footer = footer[..footer.IndexOf("</tfoot>", StringComparison.Ordinal)];
        model.Summary.CampOrders.Should().ContainSingle();
        model.Summary.CampTotalDueEur.Should().Be(2469m);
        model.Summary.CampPaymentsTotalEur.Should().Be(1234.5m);
        model.Summary.CampBalanceEur.Should().Be(1234.5m);
        footer.Should().Contain("1 camps");
        footer.Should().Contain(HtmlEncoder.Default.Encode(due));
        footer.Split(HtmlEncoder.Default.Encode(paidAndBalance)).Should().HaveCount(3);
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
        var row = new StripeReconciliationRow("cs", "pi", 100m, now, Guid.NewGuid(), "Camp", (StripeReconciliationStatus)status);
        var report = new StripeReconciliationReport(true, true, true, [row],
            orphan ? [new StripeOrphanPayment("pi_orphan", Guid.NewGuid(), "Camp", 100m, now)] : []);
        report.IsReconciled.Should().Be(expectedSuccess);
        report.RecordedFailedCount.Should().Be(status == (int)StripeReconciliationStatus.RecordedFailed ? 1 : 0);
        var model = new PaymentsReconciliationViewModel { Report = report, Rows = [] };

        var html = await RenderAsync("Payments", model, viewPath: "/Views/StoreAdmin/Payments.cshtml");

        html.Contains("Stripe and the ledger are reconciled.", StringComparison.Ordinal).Should().Be(expectedSuccess);
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(75900001)]
    [Xunit.InlineData(75999999)]
    public async Task AccountPicker_OrdersChoicesByNumber_AndPreservesSelection(int? selected)
    {
        var model = new AccountPickerModel("Income", "Income", "Income account", selected,
        [
            new AccountChoice(75900008, "Gas cylinders"),
            new AccountChoice(70500000, "Services"),
            new AccountChoice(75900001, "Ice"),
        ]);

        var html = await RenderAsync("_AccountPicker", model,
            viewPath: "/Views/StoreAdmin/_AccountPicker.cshtml");
        var options = System.Text.RegularExpressions.Regex.Matches(html, "<option value=\"(?<value>[^\"]*)\"(?<attributes>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        var values = options.Select(option => option.Groups["value"].Value).ToList();
        var expected = new List<string> { "" };
        if (selected == 75999999) expected.Add("75999999");
        expected.AddRange(["70500000", "75900001", "75900008"]);

        values.Should().Equal(expected);
        options.Where(option => option.Groups["attributes"].Value.Contains("selected", StringComparison.Ordinal))
            .Select(option => option.Groups["value"].Value)
            .Should().Equal(selected is null ? [] : [selected.Value.ToString(CultureInfo.InvariantCulture)]);
        html.Contains("no longer in the chart", StringComparison.Ordinal).Should().Be(selected == 75999999);
    }

    private static IAuditViewerService? _lastAudit;

    private static async Task<string> RenderAsync(string page, object model, IReadOnlyList<AuditEvent>? history = null, string? viewPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(StorePricesViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        var audit = _lastAudit = Substitute.For<IAuditViewerService>();
        audit.GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
