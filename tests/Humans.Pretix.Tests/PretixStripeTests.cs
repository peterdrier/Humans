using System.Net;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Stripe;
using Xunit;
using static Humans.Pretix.Tests.PretixTestHost;

namespace Humans.Pretix.Tests;

/// <summary>The read-only key on the Stripe account pretix charges into: fees and payouts for reconciliation.</summary>
public sealed class PretixStripeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private const string Code = "ABCDEFGHJKLMNPQR";

    private readonly IHttpClient _stripeHttp = Substitute.For<IHttpClient>();
    private readonly Dictionary<string, Queue<string>> _stripe = new(StringComparer.Ordinal);
    private readonly List<StripeRequest> _stripeRequests = [];

    public PretixStripeTests()
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

    private static string List(string url, params string[] items) =>
        $$"""{"object":"list","url":"{{url}}","has_more":false,"data":[{{string.Join(",", items)}}]}""";

    private static string Intent(string id, string description, string code) => $$$"""
        {"id":"{{{id}}}","object":"payment_intent","description":"{{{description}}}","metadata":{"order":"1","event":"3","code":"{{{code}}}"}}
        """;

    private static object PaidWithStripe(object? details) => Order(
        Code, status: "p", total: 1000m,
        payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe", payment_date = "2027-01-02T09:59:00Z", details }]);

    [HumansFact]
    public async Task Ledger_finds_each_payment_s_pretix_order_and_its_Humans_order()
    {
        var paid = Guid.NewGuid();
        var refunded = Guid.NewGuid();
        Stripe("/v1/balance_transactions", List("/v1/balance_transactions",
            """
            {"id":"txn_c","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":100000,"fee":1440,"net":98560,"created":1798884000,
             "source":{"id":"ch_1","object":"charge","payment_intent":"pi_new","metadata":{}}}
            """,
            """
            {"id":"txn_r","object":"balance_transaction","type":"refund","reporting_category":"refund","amount":-22500,"fee":0,"net":-22500,"created":1798884100,
             "source":{"id":"re_1","object":"refund","payment_intent":"pi_old","metadata":{}}}
            """,
            """
            {"id":"txn_x","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":500,"fee":30,"net":470,"created":1798884200,
             "source":{"id":"ch_3","object":"charge","payment_intent":"pi_other","metadata":{}}}
            """));
        Stripe("/v1/payment_intents", List("/v1/payment_intents",
            Intent("pi_new", $"NOWHERE2027-{Code}", Code), Intent("pi_other", "ELSEWHERE-ZZZZ", "ZZZZ")));
        Stripe("/v1/payment_intents/pi_old", Intent("pi_old", "NOWHERE2027-OLDCODE", "OLDCODE"));
        Stripe("/v1/payouts", List("/v1/payouts"));
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.OK, new { results = new[] { new { slug = "nowhere2027" } } })
            .On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, Order(Code, status: "p", apiMeta: new { humans_order_id = paid.ToString("D") }))
            .On("GET", $"{Api}orders/OLDCODE/", HttpStatusCode.OK, Order("OLDCODE", status: "p", apiMeta: new { humans_order_id = refunded.ToString("D") }));

        var ledger = await Create(pretix, stripe: _stripeHttp).GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 100000L, 1440L, (Guid?)paid),
            ("txn_r", PaymentLedgerEntryKind.Refund, -22500L, 0L, (Guid?)refunded),
            ("txn_x", PaymentLedgerEntryKind.Charge, 500L, 30L, (Guid?)null));
        pretix.Requests.Should().OnlyContain(r => r.Route.StartsWith("GET ", StringComparison.Ordinal))
            .And.ContainSingle(r => r.Route.EndsWith("/events/", StringComparison.Ordinal));
        _stripeRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task Without_the_Stripe_key_there_is_no_ledger_and_Check_says_so()
    {
        var settings = PretixTestHost.Settings();
        settings.StripeKey = "";
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.OK, new { results = Array.Empty<object>() });
        var backend = Create(pretix, settings, _stripeHttp);

        (await backend.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await backend.CheckAsync(null, Ct)).Should().Contain(f => !f.Ok && f.Text.Contains("STRIPE_PRETIX_KEY is unset", StringComparison.Ordinal));
        _stripeRequests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_paid_order_reports_the_fee_Stripe_kept_on_its_PaymentIntent()
    {
        var pretix = new FakePretix().On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, PaidWithStripe(new { id = "pi_1", payment_method = "pm_1" }));
        Stripe("/v1/payment_intents/pi_1", """
            {"id":"pi_1","object":"payment_intent","status":"succeeded",
             "latest_charge":{"id":"ch_1","object":"charge","balance_transaction":{"id":"txn_1","object":"balance_transaction","fee":1440}}}
            """);

        var status = await Create(pretix, stripe: _stripeHttp).GetStatusAsync($"nowhere2027/{Code}", Ct);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().Be(1440);
        Uri.UnescapeDataString(_stripeRequests.Single().Uri.Query).Should().Contain("latest_charge.balance_transaction");
    }

    [HumansFact]
    public async Task A_payment_without_a_PaymentIntent_or_a_Stripe_failure_leaves_the_fee_unknown()
    {
        var pretix = new FakePretix().On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, PaidWithStripe(new { }));
        (await Create(pretix, stripe: _stripeHttp).GetStatusAsync($"nowhere2027/{Code}", Ct)).FeeMinor.Should().BeNull();
        _stripeRequests.Should().BeEmpty();

        pretix = new FakePretix().On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, PaidWithStripe(new { id = "pi_1" }));
        _stripeHttp.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new HttpRequestException("no route"));

        var status = await Create(pretix, stripe: _stripeHttp).GetStatusAsync($"nowhere2027/{Code}", Ct);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().BeNull();
    }
}
