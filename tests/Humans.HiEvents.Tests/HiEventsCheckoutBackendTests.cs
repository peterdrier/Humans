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
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 42, total: 1000m, paymentLink: "https://tickets.example/pay/42"));

        var session = await Backend().CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana García López"),
            new CheckoutLine(CheckoutLineKind.Donation, "Donation", 77500, 0m, null)), Ct);

        session.BackendOrderId.Should().Be("7:42");
        session.PaymentUrl.Should().Be(new Uri("https://tickets.example/pay/42"));
        session.AdminUrl.Should().Be(new Uri("https://tickets.example/manage/event/7/orders#order-42"));

        var (request, body) = _handler.Requests.Single();
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
        _handler.Enqueue(HttpStatusCode.Created, Order(id: 43, total: 225m, paymentLink: "https://tickets.example/pay/43"));

        var session = await Backend().CreateCheckoutAsync(RequestFor("8",
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        session.BackendOrderId.Should().Be("8:43");
        var (request, body) = _handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/8/orders/checkout");
        JsonNode.Parse(body!)!["items"]![0]!["product_id"]!.GetValue<int>().Should().Be(300);
    }

    [HumansFact]
    public async Task CreateCheckout_ForAnUnmappedEvent_ThrowsBeforeAnyCall()
    {
        var act = () => Backend().CreateCheckoutAsync(RequestFor("9",
            new CheckoutLine(CheckoutLineKind.Ticket, "General", 22500, 0.10m, "Ana")), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        _handler.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateCheckout_DonationWithoutADonationProduct_Throws()
    {
        var act = () => Backend(donationProductId: 0).CreateCheckoutAsync(Request(
            new CheckoutLine(CheckoutLineKind.Donation, "Donation", 5000, 0m, null)), Ct);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansTheory]
    [Xunit.InlineData("COMPLETED", "PAYMENT_RECEIVED", null, CheckoutState.Paid)]
    [Xunit.InlineData("CANCELLED", "AWAITING_PAYMENT", null, CheckoutState.Cancelled)]
    [Xunit.InlineData("ABANDONED", "AWAITING_PAYMENT", null, CheckoutState.Expired)]
    [Xunit.InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 10:30:00", CheckoutState.Pending)]
    [Xunit.InlineData("RESERVED", "PAYMENT_FAILED", "2027-01-02 10:30:00", CheckoutState.Pending)]
    [Xunit.InlineData("RESERVED", "AWAITING_PAYMENT", "2027-01-02 09:59:59", CheckoutState.Expired)]
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
        _handler.Enqueue(HttpStatusCode.OK, Order(id: 42, total: 225m, status: "COMPLETED", paymentStatus: "PAYMENT_RECEIVED"));
        var refundId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var result = await Backend().RefundAsync(new RefundRequest(refundId, "7:42", 22500, "resold"), Ct);

        result.State.Should().Be(RefundState.Pending);
        var (request, body) = _handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be("/api/events/7/orders/42/refund");
        var json = JsonNode.Parse(body!)!;
        json["amount"]!.GetValue<decimal>().Should().Be(225m);
        json["idempotency_key"]!.GetValue<string>().Should().Be("aaaaaaaabbbbccccddddeeeeeeeeeeee");
        json["cancel_order"]!.GetValue<bool>().Should().BeFalse();
        json["notify_buyer"]!.GetValue<bool>().Should().BeFalse();
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
    public void IsConfigured_NeedsUrlAndKey()
    {
        Backend().IsConfigured.Should().BeTrue();
        Backend(apiKey: "").IsConfigured.Should().BeFalse();
    }

    private HiEventsCheckoutBackend Backend(string apiKey = "key", int donationProductId = 200) => new(
        new HttpClient(_handler),
        Options.Create(new HiEventsSettings
        {
            ApiUrl = "https://tickets.example/api",
            ApiKey = apiKey,
            Events = new(StringComparer.Ordinal)
            {
                ["7"] = new HiEventsEventProducts { TicketProductId = 100, DonationProductId = donationProductId },
                ["8"] = new HiEventsEventProducts { TicketProductId = 300 },
            },
        }),
        new FakeClock(Now),
        NullLogger<HiEventsCheckoutBackend>.Instance);

    private static CheckoutRequest Request(params CheckoutLine[] lines) => RequestFor("7", lines);

    private static CheckoutRequest RequestFor(string? backendEventRef, params CheckoutLine[] lines) => new(
        OrderId, Guid.NewGuid(), backendEventRef, "Ana Buyer", "ana@example.org", "EUR", "es-ES", lines,
        Now.Plus(Duration.FromMinutes(30)), new Uri("https://humans.example/Tickets/Return"));

    private static object Order(
        int id,
        decimal total,
        string status = "RESERVED",
        string paymentStatus = "AWAITING_PAYMENT",
        string? paymentLink = null,
        string? reservedUntil = null,
        decimal refunded = 0m) => new
        {
            data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["event_id"] = 7,
                ["status"] = status,
                ["payment_status"] = paymentStatus,
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
