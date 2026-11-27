using System.Net;
using System.Text;
using System.Text.Json;
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

/// <summary>The Stripe account Hi.Events charges into, read through the Stripe section: fees and payouts for reconciliation.</summary>
public sealed class HiEventsStripeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private static readonly Guid HumansOrder = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FakeHiEvents _hiEvents = new();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();

    public HiEventsStripeTests() =>
        _stripe.CheckAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

    private HiEventsPlatform Platform() => new(
        new HttpClient(_hiEvents),
        Options.Create(new HiEventsSettings { ApiUrl = "https://tickets.example/api", ApiKey = "key" }),
        new FakeClock(Now),
        NullLogger<HiEventsPlatform>.Instance,
        _stripe);

    private static StripeLedgerLine Line(string id, string category, long amount, long fee, string? intent = null, int eventId = 7, int orderId = 0) =>
        new(id, Now, category, amount, fee, null, intent is null ? null : new StripeLedgerPayment(intent, null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["event_id"] = $"{eventId}", ["order_id"] = $"{orderId}" }));

    private static object PaidOrder(int id, string? externalReference, string createdAt = "2027-01-02T09:58:00.000000Z") => new
    {
        data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = "COMPLETED",
            ["payment_status"] = "PAYMENT_RECEIVED",
            ["currency"] = "EUR",
            ["total_gross"] = 225m,
            ["external_reference"] = externalReference,
            ["created_at"] = createdAt,
        },
    };

    [HumansFact]
    public async Task Ledger_EachMovement_NamesHumansOrder()
    {
        var refundedOrder = Guid.NewGuid();
        _stripe.GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(new StripeAccountLedger(
            [
                Line("txn_c", "charge", 22500, 340, "pi_new", orderId: 42),
                Line("txn_r", "refund", -5000, 0, "pi_old", orderId: 41),
                Line("txn_r2", "refund", -1000, 0, "pi_old", orderId: 41),
                Line("txn_x", "charge", 1000, 40, "pi_other", eventId: 9, orderId: 5),
                Line("txn_p", "payout", -17160, 0),
            ],
            [new StripeLedgerPayout("po_1", Now, Now, 17160, "paid", 17160, 2)]));
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        _hiEvents.On("/api/events/7/orders/41", HttpStatusCode.OK, PaidOrder(41, refundedOrder.ToString("N")));
        _hiEvents.On("/api/events/9/orders/5", HttpStatusCode.Forbidden, new { message = "no" });

        var ledger = await Platform().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 22500L, 340L, HumansOrder),
            ("txn_r", PaymentLedgerEntryKind.Refund, -5000L, 0L, refundedOrder),
            ("txn_r2", PaymentLedgerEntryKind.Refund, -1000L, 0L, refundedOrder),
            ("txn_x", PaymentLedgerEntryKind.Charge, 1000L, 40L, null),
            ("txn_p", PaymentLedgerEntryKind.Payout, -17160L, 0L, null));
        ledger.Payouts.Should().ContainSingle().Which.ContentsNetMinor.Should().Be(17160);
        _hiEvents.Calls.Should().Be(3);
    }

    [HumansFact]
    public async Task Ledger_NoStripeAccount_IsEmpty()
    {
        (await Platform().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();

        _stripe.GetAccountLedgerAsync("HiEvents", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns<StripeAccountLedger?>(_ => throw new InvalidOperationException("Stripe refused reading the HiEvents account"));
        var read = () => Platform().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct);
        await read.Should().ThrowAsync<TicketingPlatformException>().WithMessage("*refused*");
    }

    [HumansFact]
    public async Task GetStatus_PaidOrder_ReportsPaymentFee()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        _stripe.FindAccountPaymentFeeAsync("HiEvents",
                Arg.Is<IReadOnlyDictionary<string, string>>(m => m.Count == 2 && m["event_id"] == "7" && m["order_id"] == "42"),
                Instant.FromUtc(2027, 1, 2, 9, 58), Arg.Any<CancellationToken>())
            .Returns(340L);

        var status = await Platform().GetStatusAsync("7:42", Ct);

        status.State.Should().Be(PlatformOrderState.Paid);
        status.FeeMinor.Should().Be(340);
    }

    [HumansFact]
    public async Task Check_CarriesStripeFindings()
    {
        _hiEvents.On("/api/users/me", HttpStatusCode.OK, new { data = new { id = 1 } });
        _stripe.CheckAccountAsync("HiEvents", Arg.Any<CancellationToken>())
            .Returns([new StripeAccountCheck(false, "STRIPE_HIEVENTS_KEY is unset")]);

        (await Platform().CheckAsync(null, Ct)).Should().Contain(new PlatformFinding(false, "STRIPE_HIEVENTS_KEY is unset"));
    }

    private sealed class FakeHiEvents : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public void On(string path, HttpStatusCode status, object body) => _routes[path] = (status, JsonSerializer.Serialize(body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls++;
            if (!_routes.TryGetValue(path, out var answer))
                throw new InvalidOperationException($"Unexpected Hi.Events call {path}");
            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
