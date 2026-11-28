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

/// <summary>Live reads of a ticketing platform's own Stripe account: its ledger, a payment's fee, and the key check.</summary>
public sealed class StripeAccountReadTests
{
    [HumansTheory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("rk_test_bad suffix")]
    [InlineData("rk_test_secret\r\nhidden")]
    public async Task Account_UnusableKey_RefusesBeforeSdk(string key)
    {
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var account = OwnKey(key);
        var store = OwnKey(key);
        store.Name = "Store";
        var service = Service(account, store);
        account.IsConfigured.Should().BeFalse();
        (await service.GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await service.GetAccountPaymentFeeAsync("HiEvents", "pi_1", Ct)).Should().BeNull();
        (await service.FindAccountPaymentFeeAsync("HiEvents", Order42, Now, Ct)).Should().BeNull();
        (await service.ListStoreRefundsAsync("pi_1", Ct)).Should().BeNull();
        (await service.GetStoreChargeAsync("pi_1", Ct)).Should().BeNull();
        var findings = await service.CheckAccountAsync("HiEvents", Ct);
        findings.Should().ContainSingle().Which.Ok.Should().BeFalse();
        findings.Should().OnlyContain(f => !f.Text.Contains("rk_test_secret", StringComparison.Ordinal));
        _requests.Should().BeEmpty();
    }

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
    public async Task ReadLedger_Movements_CarryPaymentIntent()
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
                ("txn_p", "payout", -17160L, 0L, null, null));
        ledger.Lines[0].Payment!.Description.Should().Be("D-42");
        ledger.Payouts.Select(p => (p.Id, p.ContentsNetMinor, p.ContentsCount)).Should().Equal(
            ("po_1", 17160, 2), ("po_2", null, 0));
        _requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task Ledger_MovementMetadata_PreservesOrderReference()
    {
        Answer("/v1/balance_transactions", List("/v1/balance_transactions",
            """{"id":"txn_r","object":"balance_transaction","reporting_category":"refund","source":{"id":"re_1","object":"refund","payment_intent":"pi_1","metadata":{"order_id":"older-order"}}}"""));
        Answer("/v1/payment_intents", List("/v1/payment_intents", Intent("pi_1", "42")));
        Answer("/v1/payouts", List("/v1/payouts"));

        var ledger = await Service(OwnKey()).GetAccountLedgerAsync("HiEvents", Now, Now, Ct);

        ledger!.Lines.Single().Payment!.Metadata.Should().Contain("order_id", "older-order").And.Contain("event_id", "7");
    }

    [HumansFact]
    public async Task Check_CheckoutRequested_UsesTicketsKey()
    {
        Answer("/v1/checkout/sessions", List("/v1/checkout/sessions"));
        Answer("/v1/balance_transactions", List("/v1/balance_transactions"));
        Answer("/v1/payouts", List("/v1/payouts"));
        Answer("/v1/payment_intents", List("/v1/payment_intents"));
        Answer("/v1/charges", List("/v1/charges"));
        Answer("/v1/refunds", List("/v1/refunds"));
        Answer("/v1/disputes", List("/v1/disputes"));
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "rk_test_tickets" }),
            new CapturingLogger<StripeService>(), _http,
            Options.Create(new StripeMirrorOptions { Accounts = [OwnKey()] }));

        var findings = await service.CheckAccountAsync("HiEvents", Ct, includeCheckout: true);

        findings.Should().HaveCount(7).And.OnlyContain(f => f.Ok);
        _requests[0].AuthorizationHeader.Parameter.Should().Be("rk_test_tickets");
        _requests[0].Uri.AbsolutePath.Should().Be("/v1/checkout/sessions");
        _requests.Skip(1).Should().OnlyContain(r => r.AuthorizationHeader.Parameter == "rk_test_hievents");
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_Cancellation_RespectsCaller(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        if (cancelCaller) await caller.CancelAsync();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new TaskCanceledException("probe cancelled", null, caller.Token));

        var check = () => Service(OwnKey()).CheckAccountAsync("HiEvents", caller.Token);

        if (cancelCaller)
            await check.Should().ThrowAsync<OperationCanceledException>();
        else
            (await check()).Should().OnlyContain(f => !f.Ok && f.Text.Contains("could not be reached"));
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("rk_test_invalid key")]
    public async Task Check_UnusableCheckoutKey_KeepsReportingFindings(string key)
    {
        Answer("/v1/balance_transactions", List("/v1/balance_transactions"));
        Answer("/v1/payouts", List("/v1/payouts"));
        Answer("/v1/payment_intents", List("/v1/payment_intents"));
        Answer("/v1/charges", List("/v1/charges"));
        Answer("/v1/refunds", List("/v1/refunds"));
        Answer("/v1/disputes", List("/v1/disputes"));
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = key }),
            new CapturingLogger<StripeService>(), _http,
            Options.Create(new StripeMirrorOptions { Accounts = [OwnKey()] }));

        var findings = await service.CheckAccountAsync("HiEvents", Ct, includeCheckout: true);

        findings.Should().ContainSingle(f => !f.Ok).Which.Text.Should().Contain("STRIPE_TICKETS_KEY");
        findings.Count(f => f.Ok).Should().Be(6);
        _requests.Should().HaveCount(6).And.NotContain(r => r.Uri.AbsolutePath == "/v1/checkout/sessions");
    }

    [HumansFact]
    public async Task Check_NoEntryOrKey_SaysWhy()
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
    public async Task ReadLedger_Refused_ThrowsInvalidOperation()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(new StripeResponse(
            HttpStatusCode.Forbidden, headers.Headers, """{"error":{"type":"invalid_request_error","message":"no scope"}}"""));

        var read = () => Service(OwnKey()).GetAccountLedgerAsync("HiEvents", Now - Duration.FromDays(1), Now, Ct);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HiEvents*");
    }

    [HumansFact]
    public async Task ReadFee_OrgKey_ReadsFromCharge()
    {
        Answer("/v1/payment_intents/pi_1", Intent("pi_1", "42", fee: 1440));
        var org = new StripeMirrorAccount { Name = "Pretix", Id = "acct_pretix", ApiKey = "rk_org" };

        (await Service(org).GetAccountPaymentFeeAsync("Pretix", "pi_1", Ct)).Should().Be(1440);

        var request = _requests.Single();
        Uri.UnescapeDataString(request.Uri.Query).Should().Contain("latest_charge.balance_transaction");
        request.StripeHeaders["Stripe-Context"].Should().Be("acct_pretix");
    }

    [HumansFact]
    public async Task ReadFee_FoundByMetadata_ReportsFee()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search", Intent("pi_1", "42", fee: 340)));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromMinutes(2), Ct)).Should().Be(340);

        Uri.UnescapeDataString(_requests.Single().Uri.Query).Replace('+', ' ')
            .Should().Contain("metadata['event_id']:'7' AND metadata['order_id']:'42' AND status:'succeeded'");
    }

    [HumansFact]
    public async Task ReadFee_NotYetIndexed_IsFoundByListing()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search"));
        Answer("/v1/payment_intents", List("/v1/payment_intents",
            Intent("pi_9", "43", fee: 99), Intent("pi_8", "42", fee: 0, status: "canceled"), Intent("pi_1", "42", fee: 340)));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromMinutes(2), Ct)).Should().Be(340);
    }

    [HumansFact]
    public async Task ReadFee_OldPaymentNotFound_HasNoFee()
    {
        Answer("/v1/payment_intents/search", List("/v1/payment_intents/search"));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now - Duration.FromDays(30), Ct)).Should().BeNull();
        _requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task ReadFee_StripeFails_LeavesFeeUnknown()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new HttpRequestException("no route"));

        (await Service(OwnKey()).FindAccountPaymentFeeAsync("HiEvents", Order42, Now, Ct)).Should().BeNull();
        (await Service(OwnKey()).GetAccountPaymentFeeAsync("HiEvents", "pi_1", Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Check_RefusedScope_IsNamed()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/balance_transactions")),
            new StripeResponse(HttpStatusCode.Forbidden, headers.Headers,
                """{"error":{"type":"invalid_request_error","code":"secret_key_required","message":"no payouts"}}"""),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/payment_intents")),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/charges")),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/refunds")),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/disputes")));

        var findings = await Service(OwnKey()).CheckAccountAsync("HiEvents", Ct);

        findings.Should().SatisfyRespectively(
            balance => balance.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("STRIPE_HIEVENTS_KEY reads Balance transactions (test mode)")),
            payouts => payouts.Should().Match<StripeAccountCheck>(c => !c.Ok && c.Text.Contains("Payouts") && c.Text.Contains("403")),
            intents => intents.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("PaymentIntents")),
            charges => charges.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("Charges")),
            refunds => refunds.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("Refunds")),
            disputes => disputes.Should().Match<StripeAccountCheck>(c => c.Ok && c.Text.Contains("Disputes")));
    }

    [HumansFact]
    public async Task Check_FullLiveSecretKey_IsFlagged()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StripeResponse(HttpStatusCode.OK, headers.Headers, List("/v1/any")));

        var findings = await Service(OwnKey("sk_live_full")).CheckAccountAsync("HiEvents", Ct);

        findings[0].Should().Match<StripeAccountCheck>(c => !c.Ok && c.Text.Contains("rk_live_"));
        findings.Skip(1).Should().OnlyContain(c => c.Ok);
    }
}
