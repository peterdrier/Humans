using System.Net;
using AwesomeAssertions;
using Humans.Stripe.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;
using static Humans.Pretix.Tests.PretixTestHost;

namespace Humans.Pretix.Tests;

/// <summary>The Stripe account pretix charges into, read through the Stripe section: fees and payouts for reconciliation.</summary>
public sealed class PretixStripeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 2, 10, 0);
    private const string Code = "ABCDEFGHJKLMNPQR";

    private readonly IStripeService _stripe = NoStripeAccount();

    private static StripeLedgerLine Line(string id, string category, long amount, long fee, string intent, string description, string code) =>
        new(id, Now, category, amount, fee, null, new StripeLedgerPayment(intent, description,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["order"] = "1", ["event"] = "3", ["code"] = code }));

    private static object PaidWithStripe(object? details) => Order(
        Code, status: "p", total: 1000m,
        payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe", payment_date = "2027-01-02T09:59:00Z", details }]);

    [HumansFact]
    public async Task Ledger_finds_each_payment_s_pretix_order_and_its_Humans_order()
    {
        var paid = Guid.NewGuid();
        var refunded = Guid.NewGuid();
        _stripe.GetAccountLedgerAsync("Pretix", Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(new StripeAccountLedger(
            [
                Line("txn_c", "charge", 100000, 1440, "pi_new", $"NOWHERE2027-{Code}", Code),
                Line("txn_r", "refund", -22500, 0, "pi_old", "NOWHERE2027-OLDCODE", "OLDCODE"),
                Line("txn_x", "charge", 500, 30, "pi_other", "ELSEWHERE-ZZZZ", "ZZZZ"),
            ],
            [new StripeLedgerPayout("po_1", Now, Now, 77500, "in_transit", null, 0)]));
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.OK, new { results = new[] { new { slug = "nowhere2027" } } })
            .On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, Order(Code, status: "p", apiMeta: new { humans_order_id = paid.ToString("D") }))
            .On("GET", $"{Api}orders/OLDCODE/", HttpStatusCode.OK, Order("OLDCODE", status: "p", apiMeta: new { humans_order_id = refunded.ToString("D") }));

        var ledger = await Create(pretix, stripe: _stripe).GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 100000L, 1440L, paid),
            ("txn_r", PaymentLedgerEntryKind.Refund, -22500L, 0L, refunded),
            ("txn_x", PaymentLedgerEntryKind.Charge, 500L, 30L, null));
        ledger.Payouts.Should().ContainSingle().Which.ContentsNetMinor.Should().BeNull();
        pretix.Requests.Should().OnlyContain(r => r.Route.StartsWith("GET ", StringComparison.Ordinal))
            .And.ContainSingle(r => r.Route.EndsWith("/events/", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Without_the_Stripe_account_there_is_no_ledger_and_Check_says_so()
    {
        _stripe.CheckAccountAsync("Pretix", Arg.Any<CancellationToken>())
            .Returns([new StripeAccountCheck(false, "Stripe:MirrorAccounts has no Pretix entry")]);
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.OK, new { results = Array.Empty<object>() });
        var platform = Create(pretix, stripe: _stripe);

        (await platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Ct)).Should().BeNull();
        (await platform.CheckAsync(null, Ct)).Should().Contain(new PlatformFinding(false, "Stripe:MirrorAccounts has no Pretix entry"));
    }

    [HumansFact]
    public async Task A_paid_order_reports_the_fee_Stripe_kept_on_its_PaymentIntent()
    {
        var pretix = new FakePretix().On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, PaidWithStripe(new { id = "pi_1", payment_method = "pm_1" }));
        _stripe.GetAccountPaymentFeeAsync("Pretix", "pi_1", Arg.Any<CancellationToken>()).Returns(1440L);

        var status = await Create(pretix, stripe: _stripe).GetStatusAsync($"nowhere2027/{Code}", Ct);

        status.State.Should().Be(PlatformOrderState.Paid);
        status.FeeMinor.Should().Be(1440);
    }

    [HumansFact]
    public async Task A_payment_without_a_PaymentIntent_never_asks_Stripe()
    {
        var pretix = new FakePretix().On("GET", $"{Api}orders/{Code}/", HttpStatusCode.OK, PaidWithStripe(new { }));

        (await Create(pretix, stripe: _stripe).GetStatusAsync($"nowhere2027/{Code}", Ct)).FeeMinor.Should().BeNull();

        await _stripe.DidNotReceiveWithAnyArgs().GetAccountPaymentFeeAsync(default!, default!);
    }
}
