using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.HiEvents.Tests;

public class HiEventsCheckoutBackendTests
{
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly RecordingHandler _handler = new();

    [HumansFact]
    public async Task CreateCheckout_PostsFixedPricesAndNamesAndReturnsThePaymentLink()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 1000m, paymentLink: "https://tickets.example/pay/42"));

        var session = await Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana García López"),
            new CheckoutLine(CheckoutLineKind.Donation, "Donation", 77500, 0m, null)), Ct);

        session.BackendOrderId.Should().Be("7:42");
        session.PaymentUrl.Should().Be(new Uri("https://tickets.example/pay/42"));
        session.AdminUrl.Should().Be(new Uri("https://tickets.example/manage/event/7/orders#order-42"));

        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[0].Request.RequestUri!.ToString().Should().Be("https://tickets.example/api/events/7/products?per_page=100");
        var (request, body) = _handler.Requests[1];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://tickets.example/api/events/7/orders/checkout");
        request.Headers.Authorization!.ToString().Should().Be("Bearer key");

        var json = JsonNode.Parse(body!)!;
        json["external_reference"]!.GetValue<string>().Should().Be("11111111222233334444555555555555");
        json["first_name"]!.GetValue<string>().Should().Be("Ana Buyer");
        json["locale"]!.GetValue<string>().Should().Be("es");
        json["expires_at"]!.GetValue<string>().Should().Be("2027-01-02T10:30:00Z");
        json["send_buyer_emails"]!.GetValue<bool>().Should().BeFalse();
        json["return_url"]!.GetValue<string>().Should().Be("https://humans.example/Tickets/Return");
        json["items"]![0]!["product_id"]!.GetValue<int>().Should().Be(100);
        json["items"]![0]!["price"]!.GetValue<decimal>().Should().Be(225m);
        json["items"]![0]!["attendee"]!["first_name"]!.GetValue<string>().Should().Be("Ana García López");
        json["items"]![1]!["product_id"]!.GetValue<int>().Should().Be(200);
        json["items"]![1]!["price"]!.GetValue<decimal>().Should().Be(775m);
        json["items"]![1]!["attendee"].Should().BeNull();
    }

    [HumansFact]
    public async Task CreateCheckout_WhenTheTotalDiffers_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 247.50m, paymentLink: "https://tickets.example/pay/42"));

        var act = () => Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansTheory]
    [Xunit.InlineData(HttpStatusCode.UnprocessableEntity)]
    [Xunit.InlineData(HttpStatusCode.Conflict)]
    public async Task CreateCheckout_WhenHiEventsRefuses_Throws(HttpStatusCode status)
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(status, new { message = "no" });

        var act = () => Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansFact]
    public async Task CreateCheckout_WithoutANumericEvent_ThrowsBeforeAnyCall()
    {
        var act = () => Backend().CreateCheckoutAsync(RequestFor(null,
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateCheckout_UsesTheProductsOfTheRequestedEvent()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 300), ("GENERAL", 400)));
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 43, total: 225m, paymentLink: "https://tickets.example/pay/43"));

        var session = await Backend().CreateCheckoutAsync(RequestFor("8",
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        session.BackendOrderId.Should().Be("8:43");
        _handler.Requests[0].Request.RequestUri!.AbsolutePath.Should().Be("/api/events/8/products");
        var (request, body) = _handler.Requests[1];
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/8/orders/checkout");
        JsonNode.Parse(body!)!["items"]![0]!["product_id"]!.GetValue<int>().Should().Be(300);
    }

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(2)]
    public async Task CreateCheckout_WithoutExactlyOneCarrierProduct_ThrowsBeforeCreatingAnOrder(int ticketProducts)
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(Enumerable.Range(1, ticketProducts).Select(i => ("TICKET", i)).Append(("GENERAL", 200)).ToArray()));

        var act = () => Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        (await act.Should().ThrowAsync<TicketCheckoutException>()).WithMessage("*exactly one TICKET product*");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task CreateCheckout_WithAVisibleCarrierProduct_ThrowsBeforeCreatingAnOrder()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 100), ("GENERAL", VisibleProductId)));

        var act = () => Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        (await act.Should().ThrowAsync<TicketCheckoutException>()).WithMessage("*must be hidden*");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task CreateCheckout_DonationWithoutADonationProduct_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 100)));

        var act = () => Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Donation, "Donation", 5000, 0m, null)), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        _handler.Requests.Should().ContainSingle();
    }

    [HumansTheory]
    [Xunit.InlineData("COMPLETED", "PAYMENT_RECEIVED", null, CheckoutState.Paid)]
    [Xunit.InlineData("CANCELLED", "AWAITING_PAYMENT", null, CheckoutState.Cancelled)]
    [Xunit.InlineData("ABANDONED", "AWAITING_PAYMENT", null, CheckoutState.Expired)]
    [Xunit.InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 10:30:00", CheckoutState.Pending)]
    [Xunit.InlineData("RESERVED", "PAYMENT_FAILED", "2027-01-02 10:30:00", CheckoutState.Pending)]
    [Xunit.InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 09:59:59", CheckoutState.Expired)]
    [Xunit.InlineData("AWAITING_OFFLINE_PAYMENT", "AWAITING_OFFLINE_PAYMENT", null, CheckoutState.Pending)]
    public async Task GetStatus_MapsHiEventsStatuses(string status, string paymentStatus, string? reservedUntil, CheckoutState expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: status, paymentStatus: paymentStatus,
            reservedUntil: reservedUntil, refunded: 25.5m));

        var result = await Backend().GetStatusAsync("7:42", Ct);

        result.State.Should().Be(expected);
        result.RefundedMinor.Should().Be(2550);
        _handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42");
    }

    [HumansFact]
    public async Task Refund_SendsTheRefundIdAsIdempotencyKeyAndIsPending()
    {
        _handler.Enqueue(HttpStatusCode.OK, RefundAnswer("re_1", "pending"));
        var refundId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var result = await Backend().RefundAsync(new RefundRequest(refundId, "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(RefundState.Pending);
        result.BackendRefundId.Should().Be("re_1");
        var (request, body) = _handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/refund");
        var json = JsonNode.Parse(body!)!;
        json["amount"]!.GetValue<decimal>().Should().Be(225m);
        json["idempotency_key"]!.GetValue<string>().Should().Be("aaaaaaaabbbbccccddddeeeeeeeeeeee");
        json["cancel_order"]!.GetValue<bool>().Should().BeFalse();
        json["notify_buyer"]!.GetValue<bool>().Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("succeeded", RefundState.Succeeded)]
    [InlineData("requires_action", RefundState.Pending)]
    [InlineData("failed", RefundState.Failed)]
    [InlineData("canceled", RefundState.Failed)]
    public async Task Refund_AnswersTheStateOfTheRefundUnderItsKey(string stripeStatus, RefundState expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, RefundAnswer("re_9", stripeStatus));

        var result = await Backend().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(expected);
        result.BackendRefundId.Should().Be("re_9");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Refund_AcceptedWithoutNamingTheRefund_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        var act = () => Backend().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task Refund_NoAnswer_Throws()
    {
        _handler.Enqueue(HttpStatusCode.BadGateway, new { message = "upstream down" });

        var act = () => Backend().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task Refund_Declined_FailsWithHiEventsReason()
    {
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, new { message = "There is already a refund pending" });

        var result = await Backend().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(RefundState.Failed);
        result.FailureReason.Should().Contain("already a refund pending");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_PaidOrder_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        var act = () => Backend().CancelAsync("7:42", Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_CancelledOrder_IsANoOp()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Backend().CancelAsync("7:42", Ct);

        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_ExpiredOrder_IsCancelledInHiEvents()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "ABANDONED"));
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Backend().CancelAsync("7:42", Ct);

        _handler.Requests[1].Request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/cancel");
    }

    [HumansFact]
    public async Task Cancel_RacingAnotherCancel_IsANoOp()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "2027-01-02 10:30:00"));
        _handler.Enqueue(HttpStatusCode.Conflict, new { message = "Order already cancelled" });

        await Backend().CancelAsync("7:42", Ct);

        _handler.Requests.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Cancel_ReservedOrder_CancelsWithoutRefund()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "2027-01-02 10:30:00"));
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Backend().CancelAsync("7:42", Ct);

        var (request, body) = _handler.Requests[1];
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/cancel");
        JsonNode.Parse(body!)!["refund"]!.GetValue<bool>().Should().BeFalse();
    }

    [HumansFact]
    public async Task GetStatus_ReportsAFailedRefund()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED",
            refundStatus: "REFUND_FAILED"));

        var result = await Backend().GetStatusAsync("7:42", Ct);

        result.RefundFailed.Should().BeTrue();
    }

    [HumansFact]
    public async Task ErasePersonalData_AnonymisesTheOrder()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        await Backend().ErasePersonalDataAsync("7:42", "ana@example.org", Ct);

        var (request, _) = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/anonymize");
    }

    [HumansFact]
    public async Task ErasePersonalData_ThrowsWhenHiEventsFails()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, new { message = "Order 42 not found for event 7" });

        var act = () => Backend().ErasePersonalDataAsync("7:42", "ana@example.org", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task GetStatus_ReservedWithAnUnreadableExpiry_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "soon"));

        var act = () => Backend().GetStatusAsync("7:42", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public void IsConfigured_NeedsAnAbsoluteUrlAndKey_AndABadUrlDoesNotThrow()
    {
        Backend().IsConfigured.Should().BeTrue();
        Backend(apiKey: "").IsConfigured.Should().BeFalse();
        Backend(apiUrl: "").IsConfigured.Should().BeFalse();
        Backend(apiUrl: "tickets example/api").IsConfigured.Should().BeFalse();
        Backend(apiUrl: "ftp://tickets.example/api").IsConfigured.Should().BeFalse();
        Backend(appUrl: "tickets.example").IsConfigured.Should().BeFalse();
        Backend(appUrl: "https://admin.tickets.example/").IsConfigured.Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData("Ana Buyer", "Ana Buyer", null)]
    [Xunit.InlineData("María de los Ángeles Fernández de Córdoba y Martínez",
        "María de los Ángeles Fernández de Córdoba y", "Martínez")]
    public void SplitBuyerName_KeepsEachPartWithinFiftyCharacters(string name, string first, string? last)
    {
        HiEventsCheckoutBackend.SplitBuyerName(name).Should().Be((first, last));
    }

    [HumansFact]
    public void SplitBuyerName_CutsANameLongerThanBothParts()
    {
        var (first, last) = HiEventsCheckoutBackend.SplitBuyerName(new string('a', 60) + " " + new string('b', 60));

        first.Should().HaveLength(50);
        last.Should().HaveLength(50);
    }

    [HumansFact]
    public async Task Check_ReadyEventOnTheFork_PassesWithoutWritingAnything()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "LIVE" } });
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, new { message = "items required" });

        var findings = await Backend().CheckAsync("7", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().ContainSingle()
            .Which.Should().Contain("HIEVENTS_WEBHOOK_SECRET");
        _handler.Requests.Select(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}").Should().Equal(
            "GET /api/users/me", "GET /api/events/7", "GET /api/events/7/products",
            "POST /api/events/7/orders/checkout");
        _handler.Requests[^1].Body.Should().Be("{}");
    }

    [HumansFact]
    public async Task Check_StockHiEvents_SaysItIsNotTheFork()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "DRAFT" } });
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 100), ("TICKET", 101)));
        _handler.Enqueue(HttpStatusCode.NotFound, new { message = "not found" });

        var findings = await Backend().CheckAsync("7", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().Contain(t => t.Contains("not the Humans fork"))
            .And.Contain(t => t.Contains("DRAFT"))
            .And.Contain(t => t.Contains("2 TICKET products"))
            .And.Contain(t => t.Contains("0 GENERAL products"));
    }

    [HumansFact]
    public async Task Check_AnApiUrlServingTheWebApp_IsNotAnAcceptedKey()
    {
        _handler.EnqueueRaw(HttpStatusCode.OK, "<!doctype html><html></html>", "text/html");

        var findings = await Backend().CheckAsync(null, Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("/api"));
    }

    [HumansFact]
    public async Task Check_HiEventsCloud_IsNeverGreen()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });

        var findings = await Backend(apiUrl: "https://api.hi.events/api").CheckAsync(null, Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("Cloud"));
    }

    [HumansFact]
    public async Task Setup_WithAnAppUrlWithoutScheme_HasNoAdminLinkAndFailsTheCheck()
    {
        var backend = Backend(appUrl: "tickets.example");

        backend.Setup.AdminUrl.Should().BeNull();
        (await backend.CheckAsync(null, Ct)).Should().Contain(f => !f.Ok && f.Text.Contains("HiEvents:AppUrl"));
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Check_Unconfigured_CallsNothing()
    {
        var findings = await Backend(apiKey: "").CheckAsync("7", Ct);

        findings.Should().ContainSingle().Which.Ok.Should().BeFalse();
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public void Setup_NamesCloudUnsupportedAndNeverShowsSecrets()
    {
        var setup = Backend(apiUrl: "https://api.hi.events/api").Setup;

        setup.Hosting.Should().Contain("not supported");
        setup.Settings.Where(x => x.IsSecret).Should().OnlyContain(x => x.Value == null);
        setup.Settings.Single(x => string.Equals(x.Name, "HIEVENTS_API_KEY", StringComparison.Ordinal)).IsSet.Should().BeTrue();
        setup.WebhookPath.Should().Be("/HiEvents/Webhook");
    }

    [HumansFact]
    public async Task ListOrders_PagesThroughThePeriodAndReadsHumansIdsAndTickets()
    {
        var full = Enumerable.Range(1, 100).Select(i => ListedOrder(i, OrderId.ToString("N"), attendees: 1)).ToArray();
        _handler.Enqueue(HttpStatusCode.OK, new { data = full, meta = new { last_page = 2, total = 101 } });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { ListedOrder(100, OrderId.ToString("N"), attendees: 1), ListedOrder(101, null, attendees: 2, refunded: 50m) },
            meta = new { last_page = 2, total = 101 },
        });

        var orders = await Backend().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        orders.Should().HaveCount(101);
        orders[0].Should().Be(new BackendOrderSummary("7:1", OrderId, Instant.FromUtc(2027, 1, 5, 9, 30), CheckoutState.Paid, 22500, 1, 0, 0));
        orders[^1].HumansOrderId.Should().BeNull();
        orders[^1].TicketCount.Should().Be(2);
        orders[^1].RefundedMinor.Should().Be(5000);
        var first = Uri.UnescapeDataString(_handler.Requests[0].Request.RequestUri!.Query);
        first.Should().Contain("per_page=100").And.Contain("page=1&")
            .And.Contain("filter_fields[created_at][gte]=2027-01-01 00:00:00")
            .And.Contain("filter_fields[created_at][lt]=2027-02-01 00:00:00");
        Uri.UnescapeDataString(_handler.Requests[1].Request.RequestUri!.Query).Should().Contain("page=2&");
    }

    [HumansFact]
    public async Task ListOrders_WhenAnOrderSlipsBetweenPages_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new[] { ListedOrder(1, null, attendees: 1) }, meta = new { last_page = 2, total = 3 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new[] { ListedOrder(1, null, attendees: 1) }, meta = new { last_page = 2, total = 3 } });

        var act = () => Backend().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task ListOrders_WhenAPageFails_Throws()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, new { message = "down" });

        var act = () => Backend().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static Dictionary<string, object?> ListedOrder(int id, string? externalReference, int attendees, decimal refunded = 0m) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = "COMPLETED",
            ["payment_status"] = "PAYMENT_RECEIVED",
            ["currency"] = "EUR",
            ["total_gross"] = 225m,
            ["total_refunded"] = refunded,
            ["external_reference"] = externalReference,
            ["created_at"] = "2027-01-05T09:30:00.000000Z",
            ["attendees"] = Enumerable.Range(1, attendees).Select(a => new { id = (id * 10) + a }).ToArray(),
        };

    private HiEventsCheckoutBackend Backend(string apiKey = "key", string apiUrl = "https://tickets.example/api", string appUrl = "") => new(
        new HttpClient(_handler),
        Options.Create(new HiEventsSettings
        {
            ApiUrl = apiUrl,
            AppUrl = appUrl,
            ApiKey = apiKey,
        }),
        new FakeClock(Now),
        NullLogger<HiEventsCheckoutBackend>.Instance);

    private static CheckoutRequest Request(params CheckoutLine[] lines) => RequestFor("7", lines);

    private static CheckoutRequest RequestFor(string? backendEventRef, params CheckoutLine[] lines) => new(
        OrderId, Guid.NewGuid(), backendEventRef, "Ana Buyer", "ana@example.org", "EUR", "es-ES", lines,
        Now.Plus(Duration.FromMinutes(30)), new Uri("https://humans.example/Tickets/Return"));

    private const int VisibleProductId = 999;

    private static object Products(params (string Type, int Id)[] products) => new
    {
        data = (products.Length == 0 ? [("TICKET", 100), ("GENERAL", 200)] : products)
            .Select(p => new { id = p.Id, product_type = p.Type, title = $"Carrier {p.Id}", is_hidden = p.Id != VisibleProductId })
            .ToArray(),
    };

    private static object RefundAnswer(string refundId, string status) => new
    {
        data = new { id = 42, status = "COMPLETED", currency = "EUR", total_gross = 225m },
        meta = new { refund = new { id = refundId, status } },
    };

    private static object Order(
        int id,
        decimal total,
        string status = "RESERVED",
        string paymentStatus = "AWAITING_PAYMENT",
        string? paymentLink = null,
        string? reservedUntil = null,
        decimal refunded = 0m,
        string? refundStatus = null) => new
        {
            data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["event_id"] = 7,
                ["status"] = status,
                ["payment_status"] = paymentStatus,
                ["refund_status"] = refundStatus,
                ["currency"] = "EUR",
                ["total_gross"] = total,
                ["total_refunded"] = refunded,
                ["payment_provider"] = "STRIPE",
                ["payment_link_url"] = paymentLink,
                ["reserved_until"] = reservedUntil,
            },
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        public void EnqueueRaw(HttpStatusCode status, string body, string mediaType) =>
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

        public void Enqueue(HttpStatusCode status, object body) =>
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return _responses.Dequeue();
        }
    }
}
