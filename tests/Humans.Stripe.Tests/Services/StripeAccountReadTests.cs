using System.Net;
using AwesomeAssertions;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
// Compilation-unit level, so this binds to the SDK's global::Stripe. See StripeService.cs.
using Stripe;

namespace Humans.Stripe.Tests.Services;

/// <summary>Live reads of a ticket backend's own Stripe account: its ledger, a payment's fee, and the key check.</summary>
public sealed class StripeAccountReadTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);

    private readonly IHttpClient _http = Substitute.For<IHttpClient>();
    private readonly Dictionary<string, Queue<string>> _answers = new(StringComparer.Ordinal);
    private readonly List<StripeRequest> _requests = [];

    public StripeAccountReadTests()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<StripeRequest>();
            _requests.Add(request);
            var path = request.Uri.AbsolutePath;
            if (!_answers.TryGetValue(path, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Unexpected Stripe call {path}");
            using var message = new HttpResponseMessage();
            return new StripeResponse(HttpStatusCode.OK, message.Headers, queue.Dequeue());
        });
    }

    private void Answer(string path, string body)
    {
        if (!_answers.TryGetValue(path, out var queue))
            _answers[path] = queue = new Queue<string>();
        queue.Enqueue(body);
    }

    private StripeService Service(params StripeMirrorAccount[] accounts) => new(
        Options.Create(new StripeSettings()), new CapturingLogger<StripeService>(), _http,
        Options.Create(new StripeMirrorOptions { Accounts = [.. accounts] }), new FakeClock(Now));

    private static StripeMirrorAccount OwnKey(string key = "rk_test_hievents") =>
        new() { Name = "HiEvents", Id = "acct_hi", KeyVariable = "STRIPE_HIEVENTS_KEY", ApiKey = key };

    private static string List(string url, params string[] items) =>
        $$"""{"object":"list","url":"{{url}}","has_more":false,"data":[{{string.Join(",", items)}}]}""";

    private static string Intent(string id, string orderId, long? fee = null, string status = "succeeded")
    {
        var charge = fee is { } f
            ? $$$"""{"id":"ch_{{{id}}}","object":"charge","balance_transaction":{"id":"txn_{{{id}}}","object":"balance_transaction","fee":{{{f}}}}}"""
            : "null";
        return $$"""
            {"id":"{{id}}","object":"payment_intent","status":"{{status}}","description":"D-{{orderId}}",
             "metadata":{"event_id":"7","order_id":"{{orderId}}"},"latest_charge":{{charge}}}
            """;
    }

    private static readonly Dictionary<string, string> Order42 = new(StringComparer.Ordinal) { ["event_id"] = "7", ["order_id"] = "42" };

    [HumansFact]
    public async Task Ledger_carries_each_movement_s_PaymentIntent_and_each_settled_payout_s_contents()
    {
        Answer("/v1/balance_transactions", List("/v1/balance_transactions",
            """
            {"id":"txn_c","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":22500,"fee":340,"net":22160,"created":1798884000,
             "source":{"id":"ch_1","object":"charge","payment_intent":"pi_new","metadata":{}}}
            """,
            """
            {"id":"txn_r","object":"balance_transaction","type":"refund","reporting_category":"refund","amount":-5000,"fee":0,"net":-5000,"created":1798884100,
             "source":{"id":"re_1","object":"refund","payment_intent":"pi_old","metadata":{}}}
            """,
            """
            {"id":"txn_p","object":"balance_transaction","type":"payout","reporting_category":"payout","amount":-17160,"fee":0,"net":-17160,"created":1798884300,
             "source":{"id":"po_1","object":"payout"}}
            """));
        Answer("/v1/payment_intents", List("/v1/payment_intents", Intent("pi_new", "42")));
        Answer("/v1/payment_intents/pi_old", Intent("pi_old", "41"));
        Answer("/v1/payouts", List("/v1/payouts",
            """{"id":"po_1","object":"payout","amount":17160,"arrival_date":1798934400,"automatic":true,"status":"paid","reconciliation_status":"completed","created":1798884300}""",
            """{"id":"po_2","object":"payout","amount":900,"arrival_date":1798934400,"automatic":true,"status":"in_transit","reconciliation_status":"in_progress","created":1798884400}"""));
        Answer("/v1/balance_transactions", List("/v1/balance_transactions",
            """{"id":"txn_c","object":"balance_transaction","type":"charge","net":22160}""",
            """{"id":"txn_r","object":"balance_transaction","type":"refund","net":-5000}""",
            """{"id":"txn_p","object":"balance_transaction","type":"payout","net":-17160}"""));

        var ledger = await Service(OwnKey()).GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct);

        ledger!.Lines.Select(l => (l.Id, l.ReportingCategory, l.AmountMinor, l.FeeMinor, l.Payment?.PaymentIntentId, l.Payment?.Metadata.GetValueOrDefault("order_id")))
            .Should().Equal(
                ("txn_c", "charge", 22500L, 340L, "pi_new", "42"),
                ("txn_r", "refund", -5000L, 0L, "pi_old", "41"),
                ("txn_p", "payout", -17160L, 0L, (string?)null, (string?)null));
        ledger.Lines[0].Payment!.Description.Should().Be("D-42");
        ledger.Payouts.Select(p => (p.Id, p.ContentsNetMinor, p.ContentsCount)).Should().Equal(
            ("po_1", (long?)17160, 2), ("po_2", (long?)null, 0));
        _requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task An_account_without_an_entry_or_a_key_reads_nothing_and_its_check_says_why()
    {
        var unset = Service(OwnKey(key: ""));

        (await Service().GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await unset.GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await unset.GetAccountPaymentFeeAsync("HiEvents", "pi_1", Ct)).Should().BeNull();
        (await unset.FindAccountPaymentFeeAsync("HiEvents", Order42, Now, Ct)).Should().BeNull();
        (await Service().CheckAccountAsync("HiEvents", Ct)).Should().ContainSingle()
            .Which.Should().Match<StripeAccountCheck>(c => !c.Ok && c.Text.Contains("no HiEvents entry"));
        (await unset.CheckAccountAsync("HiEvents", Ct)).Should().ContainSingle()
            .Which.Should().Match<StripeAccountCheck>(c => !c.Ok && c.Text.Contains("STRIPE_HIEVENTS_KEY is unset"));
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_refusal_reading_the_ledger_throws_InvalidOperationException()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(new StripeResponse(
            HttpStatusCode.Forbidden, headers.Headers, """{"error":{"type":"invalid_request_error","message":"no scope"}}"""));

        var read = () => Service(OwnKey()).GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HiEvents*");
    }

    [HumansFact]
    public async Task A_payment_s_fee_is_read_from_its_charge_through_the_org_key()
    {
        Answer("/v1/payment_intents/pi_1", Intent("pi_1", "42", fee: 1440));
        var org = new StripeMirrorAccount { Name = "Pretix", Id = "acct_pretix", ApiKey = "rk_org" };

        (await Service(org).GetAccountPaymentFeeAsync("Pretix", "pi_1", Ct)).Should().Be(1440);

        var request = _requests.Single();
        Uri.UnescapeDataString(request.Uri.Query).Should().Contain("latest_charge.balance_transaction");
        request.StripeHeaders["Stripe-Context"].Should().Be("acct_pretix");
    }

    [HumansFact]
    public async Task A_payment_found_by_metadata_reports_its_fee()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search", Intent("pi_1", "42", fee: 340)));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromMinutes(2), Ct)).Should().Be(340);

        Uri.UnescapeDataString(_requests.Single().Uri.Query).Replace('+', ' ')
            .Should().Contain("metadata['event_id']:'7' AND metadata['order_id']:'42' AND status:'succeeded'");
    }

    [HumansFact]
    public async Task A_payment_search_has_not_indexed_yet_is_found_by_listing_since_it_was_made()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search"));
        Answer("/v1/payment_intents", List("/v1/payment_intents",
            Intent("pi_9", "43", fee: 99), Intent("pi_8", "42", fee: 0, status: "canceled"), Intent("pi_1", "42", fee: 340)));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromMinutes(2), Ct)).Should().Be(340);
    }

    [HumansFact]
    public async Task An_old_payment_search_cannot_find_is_left_without_a_fee()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search"));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromDays(30), Ct)).Should().BeNull();
        _requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task A_Stripe_failure_leaves_the_fee_unknown()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new HttpRequestException("no route"));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now, Ct)).Should().BeNull();
        (await Service(OwnKey()).GetAccountPaymentFeeAsync("HiEvents", "pi_1", Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Check_reads_once_per_scope_and_names_a_refused_one()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/balance_transactions")),
            new StripeResponse(HttpStatusCode.Forbidden, headers.Headers,
                """{"error":{"type":"invalid_request_error","code":"secret_key_required","message":"no payouts"}}"""),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/payment_intents")));

        var findings = await Service(OwnKey()).CheckAccountAsync("HiEvents", Ct);

        findings.Should().SatisfyRespectively(
            balance => balance.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("STRIPE_HIEVENTS_KEY reads Balance transactions (test mode)")),
            payouts => payouts.Should().Match<StripeAccountCheck>(c => !c.Ok && c.Text.Contains("Payouts") && c.Text.Contains("403")),
            intents => intents.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("PaymentIntents")));
    }
}
