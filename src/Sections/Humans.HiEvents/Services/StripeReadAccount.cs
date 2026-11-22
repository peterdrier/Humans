using Humans.Tickets.Contracts;
using NodaTime;
using Stripe;

namespace Humans.HiEvents.Services;

/// <summary>
/// The Stripe account Hi.Events charges into, read with a key that can only read, so Tickets reconciles its fees and
/// payouts the way it does Box Office's. Nothing here moves money.
/// </summary>
internal sealed class StripeReadAccount(string key, string keyVariable, IHttpClient? http = null)
{
    public bool IsConfigured => !string.IsNullOrEmpty(key);

    private string ModeLabel => key.StartsWith("sk_test_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal)
        ? "test mode"
        : "live mode";

    private StripeClient Client() => IsConfigured
        ? new StripeClient(key, httpClient: http)
        : throw new TicketCheckoutException($"{keyVariable} is unset.");

    /// <summary>
    /// Every balance movement and payout created in [<paramref name="from"/>, <paramref name="to"/>). A movement's order
    /// is what <paramref name="orderOf"/> finds for the PaymentIntent behind its charge, refund or dispute, asked once per
    /// intent: those created from a day before the period are listed once, older ones read one by one.
    /// </summary>
    public async Task<PaymentLedger> GetLedgerAsync(
        Instant from, Instant to, Func<PaymentIntent, CancellationToken, Task<Guid?>> orderOf, CancellationToken ct)
    {
        var client = Client();
        try
        {
            var period = new DateRangeOptions { GreaterThanOrEqual = from.ToDateTimeUtc(), LessThan = to.ToDateTimeUtc() };
            var transactions = new List<BalanceTransaction>();
            await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                               new BalanceTransactionListOptions { Created = period, Limit = 100, Expand = ["data.source"] },
                               cancellationToken: ct))
                transactions.Add(t);

            var intents = new Dictionary<string, PaymentIntent>(StringComparer.Ordinal);
            await foreach (var intent in new PaymentIntentService(client).ListAutoPagingAsync(
                               new PaymentIntentListOptions
                               {
                                   Created = new DateRangeOptions
                                   {
                                       GreaterThanOrEqual = (from - Duration.FromDays(1)).ToDateTimeUtc(),
                                       LessThan = to.ToDateTimeUtc(),
                                   },
                                   Limit = 100,
                               },
                               cancellationToken: ct))
                intents[intent.Id] = intent;

            var orderOfIntent = new Dictionary<string, Guid?>(StringComparer.Ordinal);
            var entries = new List<PaymentLedgerEntry>();
            foreach (var t in transactions)
            {
                var intentId = t.Source switch
                {
                    Charge c => c.PaymentIntentId,
                    Refund r => r.PaymentIntentId,
                    Dispute d => d.PaymentIntentId,
                    _ => null,
                };
                Guid? orderId = null;
                if (intentId is not null && !orderOfIntent.TryGetValue(intentId, out orderId))
                {
                    if (!intents.TryGetValue(intentId, out var intent))
                        intent = await new PaymentIntentService(client).GetAsync(intentId, cancellationToken: ct);
                    orderId = await orderOf(intent, ct);
                    orderOfIntent[intentId] = orderId;
                }

                entries.Add(new PaymentLedgerEntry(t.Id, InstantOf(t.Created), KindOf(t), t.Amount, t.Fee, orderId, t.Description));
            }

            var payouts = new List<PaymentPayout>();
            await foreach (var payout in new PayoutService(client).ListAutoPagingAsync(
                               new PayoutListOptions { Created = period, Limit = 100 }, cancellationToken: ct))
            {
                long? contentsNet = null;
                var contentsCount = 0;
                // Stripe lists what a payout contains only for automatic payouts, and in full only once it has
                // finished reconciling them; before that a partial list would read as a mismatch.
                if (payout.Automatic && string.Equals(payout.ReconciliationStatus, "completed", StringComparison.Ordinal))
                {
                    contentsNet = 0;
                    await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                                       new BalanceTransactionListOptions { Payout = payout.Id, Limit = 100 }, cancellationToken: ct))
                    {
                        if (string.Equals(t.Type, "payout", StringComparison.Ordinal))
                            continue;
                        contentsNet += t.Net;
                        contentsCount++;
                    }
                }

                payouts.Add(new PaymentPayout(
                    payout.Id, InstantOf(payout.Created), InstantOf(payout.ArrivalDate), payout.Amount, payout.Status,
                    contentsNet, contentsCount));
            }

            return new PaymentLedger(entries, payouts);
        }
        catch (StripeException ex)
        {
            throw new TicketCheckoutException($"Stripe could not list the balance: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The succeeded payment Stripe's search finds for <paramref name="query"/>, with its charge's balance transaction;
    /// null when there is none. Search trails a new payment by up to a minute, so while the order is younger than
    /// <see cref="SearchLag"/> the PaymentIntents created since <paramref name="since"/> are listed too, and the first one
    /// <paramref name="matches"/> accepts is taken. Throws <see cref="StripeException"/>.
    /// </summary>
    public async Task<PaymentIntent?> FindPaymentAsync(
        string query, Instant since, Instant now, Func<PaymentIntent, bool> matches, CancellationToken ct)
    {
        var service = new PaymentIntentService(Client());
        var found = (await service.SearchAsync(new PaymentIntentSearchOptions
        {
            Query = $"{query} AND status:'succeeded'",
            Expand = ["data.latest_charge.balance_transaction"],
        }, cancellationToken: ct)).Data.FirstOrDefault();
        if (found is not null || now - since > SearchLag)
            return found;

        await foreach (var intent in service.ListAutoPagingAsync(new PaymentIntentListOptions
                       {
                           Created = new DateRangeOptions { GreaterThanOrEqual = (since - Duration.FromMinutes(1)).ToDateTimeUtc() },
                           Limit = 100,
                           Expand = ["data.latest_charge.balance_transaction"],
                       }, cancellationToken: ct))
        {
            if (string.Equals(intent.Status, "succeeded", StringComparison.Ordinal) && matches(intent))
                return intent;
        }

        return null;
    }

    private static readonly Duration SearchLag = Duration.FromHours(2);

    /// <summary>What Stripe kept on the payment; null until its charge has a balance transaction.</summary>
    public static long? FeeOf(PaymentIntent intent) => intent.LatestCharge?.BalanceTransaction?.Fee;

    /// <summary>The backends page's Test: one read per scope the reconciliation needs. Never throws.</summary>
    public async Task<IReadOnlyList<CheckoutBackendFinding>> CheckAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            return [new(false, $"{keyVariable} is unset: this backend's Stripe fees and payouts are not reconciled.")];

        var client = Client();
        return
        [
            await ProbeAsync($"Balance transactions ({ModeLabel})",
                () => new BalanceTransactionService(client).ListAsync(new BalanceTransactionListOptions { Limit = 1 }, cancellationToken: ct)),
            await ProbeAsync("Payouts",
                () => new PayoutService(client).ListAsync(new PayoutListOptions { Limit = 1 }, cancellationToken: ct)),
            await ProbeAsync("PaymentIntents",
                () => new PaymentIntentService(client).ListAsync(new PaymentIntentListOptions { Limit = 1 }, cancellationToken: ct)),
        ];
    }

    private async Task<CheckoutBackendFinding> ProbeAsync(string what, Func<Task> read)
    {
        try
        {
            await read();
            return new(true, $"{keyVariable} reads {what}.");
        }
        catch (StripeException ex)
        {
            return new(false, $"Stripe refused {keyVariable} reading {what} ({(int)ex.HttpStatusCode} {ex.StripeError?.Code ?? ex.StripeError?.Type ?? "error"}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new(false, $"Stripe could not be reached: {ex.Message}");
        }
    }

    private static Instant InstantOf(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    /// <summary>By Stripe's reporting category, which groups its many transaction types the way its reports do.</summary>
    private static PaymentLedgerEntryKind KindOf(BalanceTransaction t) => t.ReportingCategory switch
    {
        "charge" or "charge_failure" or "partial_capture_reversal" => PaymentLedgerEntryKind.Charge,
        "refund" or "refund_failure" => PaymentLedgerEntryKind.Refund,
        "dispute" or "dispute_reversal" => PaymentLedgerEntryKind.Dispute,
        "fee" => PaymentLedgerEntryKind.Fee,
        "payout" or "payout_reversal" => PaymentLedgerEntryKind.Payout,
        _ => PaymentLedgerEntryKind.Other,
    };
}
