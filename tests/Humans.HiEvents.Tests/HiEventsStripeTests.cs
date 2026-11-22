using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Stripe;
using Xunit;

namespace Humans.HiEvents.Tests;

/// <summary>The read-only key on the Stripe account Hi.Events charges into: fees and payouts for reconciliation.</summary>
public sealed class HiEventsStripeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private static readonly Guid HumansOrder = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FakeHiEvents _hiEvents = new();
    private readonly IHttpClient _stripeHttp = Substitute.For<IHttpClient>();
    private readonly Dictionary<string, Queue<string>> _stripe = new(StringComparer.Ordinal);
    private readonly List<StripeRequest> _stripeRequests = [];

    public HiEventsStripeTests()
    {
        _stripeHttp.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<StripeRequest>();
            _stripeRequests.Add(request);
            var path = request.Uri.AbsolutePath;
            if (!_stripe.TryGetValue(path, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Unexpected Stripe call {path}");
            using var message = new HttpResponseMessage();
            return new StripeResponse(HttpStatusCode.OK, message.Headers, queue.Dequeue());
        });
    }

    private void Stripe(string path, string body)
    {
        if (!_stripe.TryGetValue(path, out var queue))
            _stripe[path] = queue = new Queue<string>();
        queue.Enqueue(body);
    }

    private HiEventsCheckoutBackend Backend(string stripeKey = "rk_test_hievents") => new(
        new HttpClient(_hiEvents),
        Options.Create(new HiEventsSettings { ApiUrl = "https://tickets.example/api", ApiKey = "key", StripeKey = stripeKey }),
        new FakeClock(Now),
        NullLogger<HiEventsCheckoutBackend>.Instance,
        _stripeHttp);

    private static string List(string url, params string[] items) =>
        $$"""{"object":"list","url":"{{url}}","has_more":false,"data":[{{string.Join(",", items)}}]}""";

    private static string Intent(string id, int eventId, int orderId, long? fee = null, string status = "succeeded")
    {
        var charge = fee is { } f
            ? $$$"""{"id":"ch_{{{id}}}","object":"charge","balance_transaction":{"id":"txn_{{{id}}}","object":"balance_transaction","fee":{{{f}}}}}"""
            : "null";
        return $$"""
            {"id":"{{id}}","object":"payment_intent","status":"{{status}}",
             "metadata":{"event_id":"{{eventId}}","order_id":"{{orderId}}"},"latest_charge":{{charge}}}
            """;
    }

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
    public async Task Ledger_ties_each_movement_to_the_Humans_order_of_its_HiEvents_order()
    {
        Stripe("/v1/balance_transactions", List("/v1/balance_transactions",
            """
            {"id":"txn_c","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":22500,"fee":340,"net":22160,"created":1798884000,
             "source":{"id":"ch_1","object":"charge","payment_intent":"pi_new","metadata":{}}}
            """,
            """
            {"id":"txn_r","object":"balance_transaction","type":"refund","reporting_category":"refund","amount":-5000,"fee":0,"net":-5000,"created":1798884100,
             "source":{"id":"re_1","object":"refund","payment_intent":"pi_old","metadata":{}}}
            """,
            """
            {"id":"txn_x","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":1000,"fee":40,"net":960,"created":1798884200,
             "source":{"id":"ch_3","object":"charge","payment_intent":"pi_other","metadata":{}}}
            """,
            """
            {"id":"txn_p","object":"balance_transaction","type":"payout","reporting_category":"payout","amount":-17160,"fee":0,"net":-17160,"created":1798884300,
             "source":{"id":"po_1","object":"payout"}}
            """));
        Stripe("/v1/payment_intents", List("/v1/payment_intents", Intent("pi_new", 7, 42), Intent("pi_other", 9, 5)));
        Stripe("/v1/payment_intents/pi_old", Intent("pi_old", 7, 41));
        Stripe("/v1/payouts", List("/v1/payouts",
            """{"id":"po_1","object":"payout","amount":17160,"arrival_date":1798934400,"automatic":true,"status":"paid","reconciliation_status":"completed","created":1798884300}"""));
        Stripe("/v1/balance_transactions", List("/v1/balance_transactions",
            """{"id":"txn_c","object":"balance_transaction","type":"charge","net":22160}""",
            """{"id":"txn_r","object":"balance_transaction","type":"refund","net":-5000}""",
            """{"id":"txn_p","object":"balance_transaction","type":"payout","net":-17160}"""));
        var refundedOrder = Guid.NewGuid();
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        _hiEvents.On("/api/events/7/orders/41", HttpStatusCode.OK, PaidOrder(41, refundedOrder.ToString("N")));
        _hiEvents.On("/api/events/9/orders/5", HttpStatusCode.Forbidden, new { message = "no" });

        var ledger = await Backend().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 22500L, 340L, (Guid?)HumansOrder),
            ("txn_r", PaymentLedgerEntryKind.Refund, -5000L, 0L, (Guid?)refundedOrder),
            ("txn_x", PaymentLedgerEntryKind.Charge, 1000L, 40L, (Guid?)null),
            ("txn_p", PaymentLedgerEntryKind.Payout, -17160L, 0L, (Guid?)null));
        ledger.Payouts.Should().ContainSingle().Which.ContentsNetMinor.Should().Be(17160);
        _stripeRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task Without_the_Stripe_key_there_is_no_ledger_and_no_fee_and_Stripe_is_never_called()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));

        (await Backend(stripeKey: "").GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await Backend(stripeKey: "").GetStatusAsync("7:42", Ct)).FeeMinor.Should().BeNull();
        _stripeRequests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_paid_order_reports_the_fee_Stripe_kept_on_its_payment()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        Stripe("/v1/payment_intents/search", List("/v1/payment_intents/search", Intent("pi_1", 7, 42, fee: 340)));

        var status = await Backend().GetStatusAsync("7:42", Ct);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().Be(340);
        var query = Uri.UnescapeDataString(_stripeRequests.Single().Uri.Query).Replace('+', ' ');
        query.Should().Contain("metadata['event_id']:'7' AND metadata['order_id']:'42' AND status:'succeeded'");
    }

    [HumansFact]
    public async Task A_payment_search_has_not_indexed_yet_is_found_by_listing_since_the_order()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        Stripe("/v1/payment_intents/search", List("/v1/payment_intents/search"));
        Stripe("/v1/payment_intents", List("/v1/payment_intents",
            Intent("pi_9", 7, 43, fee: 99), Intent("pi_8", 7, 42, fee: 0, status: "canceled"), Intent("pi_1", 7, 42, fee: 340)));

        (await Backend().GetStatusAsync("7:42", Ct)).FeeMinor.Should().Be(340);
    }

    [HumansFact]
    public async Task An_old_order_search_cannot_find_is_left_without_a_fee()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N"), "2026-12-01T09:00:00.000000Z"));
        Stripe("/v1/payment_intents/search", List("/v1/payment_intents/search"));

        (await Backend().GetStatusAsync("7:42", Ct)).FeeMinor.Should().BeNull();
        _stripeRequests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task A_Stripe_failure_never_holds_up_a_paid_order()
    {
        _hiEvents.On("/api/events/7/orders/42", HttpStatusCode.OK, PaidOrder(42, HumansOrder.ToString("N")));
        _stripeHttp.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new HttpRequestException("no route"));

        var status = await Backend().GetStatusAsync("7:42", Ct);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().BeNull();
    }

    [HumansFact]
    public async Task Check_says_when_the_Stripe_key_is_unset_or_lacks_a_scope()
    {
        _hiEvents.On("/api/users/me", HttpStatusCode.OK, new { data = new { id = 1 } });
        (await Backend(stripeKey: "").CheckAsync(null, Ct)).Should()
            .Contain(f => !f.Ok && f.Text.Contains("STRIPE_HIEVENTS_KEY is unset", StringComparison.Ordinal));

        using var headers = new HttpResponseMessage();
        _stripeHttp.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/balance_transactions")),
            new StripeResponse(HttpStatusCode.Forbidden, headers.Headers,
                """{"error":{"type":"invalid_request_error","code":"secret_key_required","message":"no payouts"}}"""),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/payment_intents")));

        var findings = await Backend().CheckAsync(null, Ct);

        findings.Should().Contain(f => f.Ok && f.Text.Contains("Balance transactions (test mode)", StringComparison.Ordinal))
            .And.Contain(f => !f.Ok && f.Text.Contains("Payouts", StringComparison.Ordinal))
            .And.Contain(f => f.Ok && f.Text.Contains("PaymentIntents", StringComparison.Ordinal));
    }

    [HumansFact]
    public void Setup_lists_the_Stripe_key_without_its_value()
    {
        var setting = Backend().Setup.Settings.Single(s => string.Equals(s.Name, "STRIPE_HIEVENTS_KEY", StringComparison.Ordinal));

        setting.IsSecret.Should().BeTrue();
        setting.IsSet.Should().BeTrue();
        setting.Value.Should().BeNull();
    }

    private sealed class FakeHiEvents : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

        public void On(string path, HttpStatusCode status, object body) => _routes[path] = (status, JsonSerializer.Serialize(body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!_routes.TryGetValue(path, out var answer))
                throw new InvalidOperationException($"Unexpected Hi.Events call {path}");
            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
