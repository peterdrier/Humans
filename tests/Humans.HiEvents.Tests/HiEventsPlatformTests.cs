using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Humans.HiEvents.Services;
using Humans.Stripe.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.HiEvents.Tests;

public class HiEventsPlatformTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly RecordingHandler _handler = new();
    private readonly IStripeService _stripe = NoStripeAccount();

    private static IStripeService NoStripeAccount()
    {
        var stripe = Substitute.For<IStripeService>();
        stripe.CheckAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        return stripe;
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_CallerCancels_Propagates(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        if (cancelCaller) await caller.CancelAsync();
        _handler.Failure = new TaskCanceledException("probe cancelled", null, caller.Token);

        var check = () => Platform().CheckAsync(null, caller.Token);

        if (cancelCaller)
            await check.Should().ThrowAsync<OperationCanceledException>();
        else
            (await check()).Should().Contain(f => !f.Ok && f.Text.Contains("could not be read"));
    }

    [HumansFact]
    public async Task CreateOrder_ValidOrder_ReturnsPaymentLink()
    {
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = 100, product_type = "TICKET", is_hidden = true } },
            meta = new { last_page = 2, total = 2 },
        });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = 200, product_type = "GENERAL", is_hidden = true } },
            meta = new { last_page = 2, total = 2 },
        });
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 1000m, paymentLink: "https://tickets.example/pay/42"));

        var session = await Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana García López"),
            new PlatformOrderLine(OrderLineKind.Donation, "Donation", 77500, 0m, null)), Ct);

        session.PlatformOrderId.Should().Be("7:42");
        session.PaymentUrl.Should().Be(new Uri("https://tickets.example/pay/42"));
        session.AdminUrl.Should().Be(new Uri("https://tickets.example/manage/event/7/orders#order-42"));

        _handler.Requests.Should().HaveCount(3);
        _handler.Requests[0].Request.RequestUri!.ToString().Should().Be("https://tickets.example/api/events/7/products?per_page=100&page=1");
        _handler.Requests[1].Request.RequestUri!.Query.Should().Contain("page=2");
        var (request, body) = _handler.Requests[2];
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
    public async Task CreateOrder_WhenTheTotalDiffers_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 247.50m, paymentLink: "https://tickets.example/pay/42"));

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansFact]
    public async Task CreateOrder_WhenTheEventSellsInAnotherCurrency_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 225m, paymentLink: "https://tickets.example/pay/42", currency: "USD"));

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansTheory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task CreateOrder_WhenHiEventsRefuses_Throws(HttpStatusCode status)
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(status, new { message = "no" });

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansFact]
    public async Task CreateOrder_WithoutANumericEvent_ThrowsBeforeAnyCall()
    {
        var act = () => Platform().CreateOrderAsync(RequestFor(null,
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateOrder_UsesTheProductsOfTheRequestedEvent()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 300), ("GENERAL", 400)));
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 43, total: 225m, paymentLink: "https://tickets.example/pay/43"));

        var session = await Platform().CreateOrderAsync(RequestFor("8",
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        session.PlatformOrderId.Should().Be("8:43");
        _handler.Requests[0].Request.RequestUri!.AbsolutePath.Should().Be("/api/events/8/products");
        var (request, body) = _handler.Requests[1];
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/8/orders/checkout");
        JsonNode.Parse(body!)!["items"]![0]!["product_id"]!.GetValue<int>().Should().Be(300);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CreateOrder_NotOneCarrierProduct_Throws(int ticketProducts)
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(Enumerable.Range(1, ticketProducts).Select(i => ("TICKET", i)).Append(("GENERAL", 200)).ToArray()));

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        (await act.Should().ThrowAsync<TicketingPlatformException>()).WithMessage("*exactly one TICKET product*");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task CreateOrder_VisibleCarrierProduct_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = 100, product_type = "TICKET", is_hidden = true } },
            meta = new { last_page = 2, total = 2 },
        });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = VisibleProductId, product_type = "GENERAL", is_hidden = false } },
            meta = new { last_page = 2, total = 2 },
        });

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        (await act.Should().ThrowAsync<TicketingPlatformException>()).WithMessage("*must be hidden*");
        _handler.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Request.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task CreateOrder_DonationWithoutADonationProduct_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 100)));

        var act = () => Platform().CreateOrderAsync(Request(
            new PlatformOrderLine(OrderLineKind.Donation, "Donation", 5000, 0m, null)), Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        _handler.Requests.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData("COMPLETED", "PAYMENT_RECEIVED", null, PlatformOrderState.Paid)]
    [InlineData("CANCELLED", "AWAITING_PAYMENT", null, PlatformOrderState.Cancelled)]
    [InlineData("ABANDONED", "AWAITING_PAYMENT", null, PlatformOrderState.Expired)]
    [InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 10:30:00", PlatformOrderState.Pending)]
    [InlineData("RESERVED", "PAYMENT_FAILED", "2027-01-02 10:30:00", PlatformOrderState.Pending)]
    [InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 09:59:59", PlatformOrderState.Expired)]
    [InlineData("AWAITING_OFFLINE_PAYMENT", "AWAITING_OFFLINE_PAYMENT", null, PlatformOrderState.Pending)]
    public async Task GetStatus_MapsHiEventsStatuses(string status, string paymentStatus, string? reservedUntil, PlatformOrderState expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: status, paymentStatus: paymentStatus,
            reservedUntil: reservedUntil, refunded: 25.5m));

        var result = await Platform().GetStatusAsync("7:42", Ct);

        result.State.Should().Be(expected);
        result.RefundedMinor.Should().Be(2550);
        _handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42");
    }

    [HumansFact]
    public async Task Refund_SendsRefundIdAsIdempotencyKey()
    {
        _handler.Enqueue(HttpStatusCode.OK, RefundAnswer("re_1", "pending"));
        var refundId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var result = await Platform().RefundAsync(new RefundRequest(refundId, "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(RefundState.Pending);
        result.PlatformRefundId.Should().Be("re_1");
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

        var result = await Platform().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(expected);
        result.PlatformRefundId.Should().Be("re_9");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Refund_InAStateStripeDoesNotDocument_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, RefundAnswer("re_9", "processing"));

        var act = () => Platform().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task Refund_AcceptedWithoutNamingTheRefund_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        var act = () => Platform().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task Refund_NoAnswer_Throws()
    {
        _handler.Enqueue(HttpStatusCode.BadGateway, new { message = "upstream down" });

        var act = () => Platform().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task Refund_Declined_FailsWithHiEventsReason()
    {
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, new { message = "There is already a refund pending" });

        var result = await Platform().RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(RefundState.Failed);
        result.FailureReason.Should().Contain("already a refund pending");
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_PaidOrder_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        var act = () => Platform().CancelAsync("7:42", Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_CancelledOrder_IsANoOp()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Platform().CancelAsync("7:42", Ct);

        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_ExpiredOrder_IsCancelledInHiEvents()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "ABANDONED"));
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Platform().CancelAsync("7:42", Ct);

        _handler.Requests[1].Request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/cancel");
    }

    [HumansFact]
    public async Task Cancel_RacingAnotherCancel_IsANoOp()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "2027-01-02 10:30:00"));
        _handler.Enqueue(HttpStatusCode.Conflict, new { message = "Order already cancelled" });

        await Platform().CancelAsync("7:42", Ct);

        _handler.Requests.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Cancel_ReservedOrder_CancelsWithoutRefund()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "2027-01-02 10:30:00"));
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "CANCELLED"));

        await Platform().CancelAsync("7:42", Ct);

        var (request, body) = _handler.Requests[1];
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/cancel");
        JsonNode.Parse(body!)!["refund"]!.GetValue<bool>().Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("RESERVED", "2027-01-02 10:30:00")]
    [InlineData("RESERVED", "2027-01-02 09:00:00")]
    [InlineData("ABANDONED", null)]
    public async Task GetStatus_PaymentOnItsWay_IsProcessing(string status, string? reservedUntil)
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: status, reservedUntil: reservedUntil,
            processingAt: "2027-01-02T09:40:00.000000Z"));

        var result = await Platform().GetStatusAsync("7:42", Ct);

        result.State.Should().Be(PlatformOrderState.Processing);
    }

    [HumansTheory]
    [InlineData("COMPLETED", "PAYMENT_RECEIVED", PlatformOrderState.Paid)]
    [InlineData("CANCELLED", "AWAITING_PAYMENT", PlatformOrderState.Cancelled)]
    public async Task GetStatus_ASettledOrClosedOrder_IsNotProcessing(string status, string paymentStatus, PlatformOrderState expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: status, paymentStatus: paymentStatus,
            processingAt: "2027-01-02T09:40:00.000000Z"));

        var result = await Platform().GetStatusAsync("7:42", Ct);

        result.State.Should().Be(expected);
    }

    [HumansFact]
    public async Task Cancel_APaymentOnItsWay_ThrowsWithoutCancelling()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "2027-01-02 09:00:00",
            processingAt: "2027-01-02T08:50:00.000000Z"));

        var act = () => Platform().CancelAsync("7:42", Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        _handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetStatus_ReportsWhatAChargebackHoldsBack()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED",
            chargedBack: 225m));

        var result = await Platform().GetStatusAsync("7:42", Ct);

        result.State.Should().Be(PlatformOrderState.Paid);
        result.ChargedBackMinor.Should().Be(22500);
        result.RefundedMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task GetStatus_ReportsAFailedRefund()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED",
            refundStatus: "REFUND_FAILED"));

        var result = await Platform().GetStatusAsync("7:42", Ct);

        result.RefundFailed.Should().BeTrue();
    }

    [HumansFact]
    public async Task ErasePersonalData_AnonymisesTheOrder()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));

        await Platform().ErasePersonalDataAsync("7:42", "ana@example.org", Ct);

        var (request, _) = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/anonymize");
    }

    [HumansFact]
    public async Task ErasePersonalData_ThrowsWhenHiEventsFails()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, new { message = "Order 42 not found for event 7" });

        var act = () => Platform().ErasePersonalDataAsync("7:42", "ana@example.org", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task GetStatus_AnUnknownOrderStatus_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "ON_HOLD"));

        var act = () => Platform().GetStatusAsync("7:42", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task GetStatus_ReservedWithAnUnreadableExpiry_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, reservedUntil: "soon"));

        var act = () => Platform().GetStatusAsync("7:42", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public void IsConfigured_BadUrl_IsFalse()
    {
        Platform().IsConfigured.Should().BeTrue();
        Platform(apiKey: "").IsConfigured.Should().BeFalse();
        Platform(apiUrl: "").IsConfigured.Should().BeFalse();
        Platform(apiUrl: "tickets example/api").IsConfigured.Should().BeFalse();
        Platform(apiUrl: "ftp://tickets.example/api").IsConfigured.Should().BeFalse();
        Platform(appUrl: "tickets.example").IsConfigured.Should().BeFalse();
        Platform(appUrl: "https://admin.tickets.example/").IsConfigured.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("key\r\nnext")]
    [InlineData("key\nnext")]
    [InlineData("key\rnext")]
    [InlineData("   ")]
    public async Task Check_InvalidApiKey_RemainsResolvableWithoutRequests(string apiKey)
    {
        var platform = Platform(apiKey: apiKey);

        platform.IsConfigured.Should().BeFalse();
        var findings = await platform.CheckAsync("7", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("HIEVENTS_API_KEY", StringComparison.Ordinal));
        _handler.Requests.Should().BeEmpty();
        _stripe.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("create")]
    [InlineData("status")]
    [InlineData("refund")]
    [InlineData("cancel")]
    [InlineData("erase")]
    [InlineData("list")]
    public async Task OrderOperation_InvalidApiKey_RefusesBeforeRequests(string operation)
    {
        var platform = Platform(apiKey: "   ");
        Func<Task> act = operation switch
        {
            "create" => () => platform.CreateOrderAsync(Request(), Ct),
            "status" => () => platform.GetStatusAsync("7:42", Ct),
            "refund" => () => platform.RefundAsync(new RefundRequest(Guid.NewGuid(), "7:42", 22500, "resold"), Ct),
            "cancel" => () => platform.CancelAsync("7:42", Ct),
            "erase" => () => platform.ErasePersonalDataAsync("7:42", "human@example.test", Ct),
            "list" => () => platform.ListOrdersAsync("7", Now, Now + Duration.FromDays(1), Ct),
            _ => throw new InvalidOperationException(operation),
        };

        await act.Should().ThrowAsync<TicketingPlatformException>();

        _handler.Requests.Should().BeEmpty();
        _stripe.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("Ana Buyer", "Ana Buyer", null)]
    [InlineData("María de los Ángeles Fernández de Córdoba y Martínez",
        "María de los Ángeles Fernández de Córdoba y", "Martínez")]
    public void SplitBuyerName_KeepsEachPartWithinFiftyCharacters(string name, string first, string? last)
    {
        HiEventsPlatform.SplitBuyerName(name).Should().Be((first, last));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitBuyerName_PreservesUnicodeAtBothPartBoundaries(bool truncateLast)
    {
        var first = truncateLast ? "Buyer" : new string('a', 49) + "𐐀";
        var last = truncateLast ? new string('b', 49) + "𐐀" : "Family";
        var name = first + " " + last + (truncateLast ? "tail" : "");

        var actual = HiEventsPlatform.SplitBuyerName(name);
        string.Equals(actual.FirstName, first, StringComparison.Ordinal).Should().BeTrue();
        string.Equals(actual.LastName, last, StringComparison.Ordinal).Should().BeTrue();
    }

    [HumansFact]
    public void SplitBuyerName_KeepsFiftyUnicodeCharactersInOnePart()
    {
        var name = string.Concat(Enumerable.Repeat("𐐀", 50));

        HiEventsPlatform.SplitBuyerName(name).Should().Be((name, (string?)null));
    }

    [HumansFact]
    public void SplitBuyerName_CutsANameLongerThanBothParts()
    {
        var (first, last) = HiEventsPlatform.SplitBuyerName(new string('a', 60) + " " + new string('b', 60));

        first.Should().HaveLength(50);
        last.Should().HaveLength(50);
    }

    [HumansFact]
    public async Task Check_ReadyEventOnTheFork_PassesWithoutWritingAnything()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "LIVE" } });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = 100, product_type = "TICKET", is_hidden = true } },
            meta = new { last_page = 2, total = 2 },
        });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { new { id = 200, product_type = "GENERAL", is_hidden = true } },
            meta = new { last_page = 2, total = 2 },
        });
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, new { message = "items required" });

        var findings = await Platform().CheckAsync("7", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().ContainSingle()
            .Which.Should().Contain("HIEVENTS_WEBHOOK_SECRET");
        _handler.Requests.Select(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}").Should().Equal(
            "GET /api/users/me", "GET /api/events/7", "GET /api/events/7/products", "GET /api/events/7/products",
            "POST /api/events/7/orders/checkout");
        _handler.Requests[^1].Body.Should().Be("{}");
    }

    [HumansFact]
    public async Task Check_AVisibleCarrier_IsNamedAsSellingPublicly()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "LIVE" } });
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", VisibleProductId), ("GENERAL", 200)));
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, new { message = "items required" });

        var findings = await Platform().CheckAsync("7", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains($"Products {VisibleProductId} are visible", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Check_StockHiEvents_SaysItIsNotTheFork()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "DRAFT" } });
        _handler.Enqueue(HttpStatusCode.OK, Products(("TICKET", 100), ("TICKET", 101)));
        _handler.Enqueue(HttpStatusCode.NotFound, new { message = "not found" });

        var findings = await Platform().CheckAsync("7", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().Contain(t => t.Contains("not the Humans fork"))
            .And.Contain(t => t.Contains("DRAFT"))
            .And.Contain(t => t.Contains("2 TICKET products"))
            .And.Contain(t => t.Contains("0 GENERAL products"));
    }

    [HumansFact]
    public async Task Check_ApiUrlServesWebApp_RejectsKey()
    {
        _handler.EnqueueRaw(HttpStatusCode.OK, "<!doctype html><html></html>", "text/html");

        var findings = await Platform().CheckAsync(null, Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("/api"));
    }

    [HumansFact]
    public async Task Check_HiEventsCloud_IsNeverGreen()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });

        var findings = await Platform(apiUrl: "https://api.hi.events/api").CheckAsync(null, Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("Cloud"));
    }

    [HumansFact]
    public async Task Setup_AppUrlWithoutScheme_HasNoAdminLink()
    {
        var platform = Platform(appUrl: "tickets.example");

        platform.Setup.AdminUrl.Should().BeNull();
        (await platform.CheckAsync(null, Ct)).Should().Contain(f => !f.Ok && f.Text.Contains("HiEvents:AppUrl"));
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Check_Unconfigured_CallsNothing()
    {
        var findings = await Platform(apiKey: "").CheckAsync("7", Ct);

        findings.Should().ContainSingle().Which.Ok.Should().BeFalse();
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public void Setup_NeverShowsSecrets()
    {
        var setup = Platform(apiUrl: "https://api.hi.events/api").Setup;

        setup.Hosting.Should().Contain("not supported");
        setup.Settings.Where(x => x.IsSecret).Should().OnlyContain(x => x.Value == null);
        setup.Settings.Single(x => string.Equals(x.Name, "HIEVENTS_API_KEY", StringComparison.Ordinal)).IsSet.Should().BeTrue();
        setup.WebhookPath.Should().Be("/HiEvents/Webhook");
    }

    [HumansFact]
    public async Task ListOrders_SeveralPages_ReadsWholePeriod()
    {
        var full = Enumerable.Range(1, 100).Select(i => ListedOrder(i, OrderId.ToString("N"), tickets: 1)).ToArray();
        _handler.Enqueue(HttpStatusCode.OK, new { data = full, meta = new { last_page = 2, total = 101 } });
        _handler.Enqueue(HttpStatusCode.OK, new
        {
            data = new[] { ListedOrder(100, OrderId.ToString("N"), tickets: 1), ListedOrder(101, null, tickets: 2, refunded: 50m, chargedBack: 175m) },
            meta = new { last_page = 2, total = 101 },
        });

        var orders = await Platform().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        orders.Should().HaveCount(101);
        orders[0].Should().Be(new PlatformOrderSummary("7:1", OrderId, Instant.FromUtc(2027, 1, 5, 9, 30), PlatformOrderState.Paid, 22500, 1, 0, 0));
        orders[^1].HumansOrderId.Should().BeNull();
        orders[^1].TicketCount.Should().Be(2);
        orders[^1].RefundedMinor.Should().Be(5000);
        orders[^1].ChargedBackMinor.Should().Be(17500);
        var first = Uri.UnescapeDataString(_handler.Requests[0].Request.RequestUri!.Query);
        first.Should().Contain("per_page=100").And.Contain("page=1&")
            .And.Contain("filter_fields[created_at][gte]=2027-01-01 00:00:00")
            .And.Contain("filter_fields[created_at][lt]=2027-02-01 00:00:00");
        Uri.UnescapeDataString(_handler.Requests[1].Request.RequestUri!.Query).Should().Contain("page=2&");
    }

    [HumansFact]
    public async Task ListOrders_WhenAnOrderSlipsBetweenPages_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new[] { ListedOrder(1, null, tickets: 1) }, meta = new { last_page = 2, total = 3 } });
        _handler.Enqueue(HttpStatusCode.OK, new { data = new[] { ListedOrder(1, null, tickets: 1) }, meta = new { last_page = 2, total = 3 } });

        var act = () => Platform().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paging_StopsAtTheSafetyCap(bool products)
    {
        if (products)
        {
            _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 1 } });
            _handler.Enqueue(HttpStatusCode.OK, new { data = new { id = 7, status = "LIVE" } });
        }
        for (var page = 1; page <= 501; page++)
            _handler.Enqueue(HttpStatusCode.OK, new { data = Array.Empty<object>(), meta = new { last_page = 501, total = 0 } });

        if (products)
            (await Platform().CheckAsync("7", Ct)).Should().Contain(f => !f.Ok && f.Text.Contains("500-page"));
        else
        {
            var act = () => Platform().ListOrdersAsync("7", Now, Now + Duration.FromDays(1), Ct);
            (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*500-page*");
        }
        _handler.Requests.Should().HaveCount(products ? 502 : 500);
    }

    [HumansFact]
    public async Task ListOrders_APageWithoutItsPaging_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { data = new[] { ListedOrder(1, null, tickets: 1) } });

        var act = () => Platform().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task ListOrders_WhenAPageFails_Throws()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, new { message = "down" });

        var act = () => Platform().ListOrdersAsync("7", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static Dictionary<string, object?> ListedOrder(int id, string? externalReference, int tickets, decimal refunded = 0m, decimal chargedBack = 0m) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = "COMPLETED",
            ["payment_status"] = "PAYMENT_RECEIVED",
            ["currency"] = "EUR",
            ["total_gross"] = 225m,
            ["total_refunded"] = refunded,
            ["total_disputed"] = chargedBack,
            ["external_reference"] = externalReference,
            ["created_at"] = "2027-01-05T09:30:00.000000Z",
            ["attendees"] = Enumerable.Range(1, tickets).Select(a => new { id = (id * 10) + a }).ToArray(),
        };

    private HiEventsPlatform Platform(string apiKey = "key", string apiUrl = "https://tickets.example/api", string appUrl = "") => new(
        new HttpClient(_handler),
        Options.Create(new HiEventsSettings
        {
            ApiUrl = apiUrl,
            AppUrl = appUrl,
            ApiKey = apiKey,
        }),
        new FakeClock(Now),
        NullLogger<HiEventsPlatform>.Instance,
        _stripe);

    [HumansTheory]
    [InlineData("ca-ES", "es")]
    [InlineData("ca", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("eu-ES", "en")]
    public async Task CreateOrder_SendsThePayPageLocale(string culture, string locale)
    {
        _handler.Enqueue(HttpStatusCode.OK, Products());
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 225m, paymentLink: "https://tickets.example/pay/42"));

        await Platform().CreateOrderAsync(
            Request(new PlatformOrderLine(OrderLineKind.Ticket, "General", 22500, 0.10m, "Ana García López")) with { Culture = culture },
            Ct);

        JsonNode.Parse(_handler.Requests[1].Body!)!["locale"]!.GetValue<string>().Should().Be(locale);
    }

    private static PlatformOrderRequest Request(params PlatformOrderLine[] lines) => RequestFor("7", lines);

    private static PlatformOrderRequest RequestFor(string? platformEventRef, params PlatformOrderLine[] lines) => new(
        OrderId, Guid.NewGuid(), platformEventRef, "Ana Buyer", "ana@example.org", "EUR", "es-ES", lines,
        Now.Plus(Duration.FromMinutes(30)), new Uri("https://humans.example/Tickets/Return"));

    private const int VisibleProductId = 999;

    private static object Products(params (string Type, int Id)[] products) => new
    {
        data = (products.Length == 0 ? [("TICKET", 100), ("GENERAL", 200)] : products)
            .Select(p => new { id = p.Id, product_type = p.Type, title = $"Carrier {p.Id}", is_hidden = p.Id != VisibleProductId })
            .ToArray(),
        meta = new { last_page = 1, total = products.Length == 0 ? 2 : products.Length },
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
        string? refundStatus = null,
        decimal chargedBack = 0m,
        string? processingAt = null,
        string currency = "EUR") => new
        {
            data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["event_id"] = 7,
                ["status"] = status,
                ["payment_status"] = paymentStatus,
                ["refund_status"] = refundStatus,
                ["currency"] = currency,
                ["total_gross"] = total,
                ["total_refunded"] = refunded,
                ["payment_provider"] = "STRIPE",
                ["payment_link_url"] = paymentLink,
                ["reserved_until"] = reservedUntil,
                ["total_disputed"] = chargedBack,
                ["payment_processing_at"] = processingAt,
            },
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public Exception? Failure { get; set; }

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
            if (Failure is { } failure) throw failure;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return _responses.Dequeue();
        }
    }
}
