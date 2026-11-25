using Humans.Base.Extensions;
using Humans.Stripe.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;
// See StripeService.cs: the bare name `Stripe` binds to this section here.
using Stripe;

namespace Humans.Stripe.Services;

/// <summary>Live reads of a named mirror account, for a ticketing platform that charges into its own Stripe account.</summary>
internal sealed partial class StripeService
{
    private static readonly Duration SearchLag = Duration.FromHours(2);

    private StripeMirrorAccount? Account(string name) => mirrorOptions?.Value.Accounts
        .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private (StripeClient Client, RequestOptions Request)? Reader(string name) => Account(name) is { IsConfigured: true } account
        ? (new StripeClient(account.ApiKey, httpClient: httpClient),
            account.UsesOrgKey ? new RequestOptions { StripeContext = account.Id } : new RequestOptions())
        : null;

    public async Task<StripeAccountLedger?> GetAccountLedgerAsync(
        string accountName, Instant from, Instant to, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (Reader(accountName) is not var (client, request))
            return null;
        try
        {
            var period = new DateRangeOptions { GreaterThanOrEqual = from.ToDateTimeUtc(), LessThan = to.ToDateTimeUtc() };
            var transactions = new List<BalanceTransaction>();
            await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                               new BalanceTransactionListOptions { Created = period, Limit = 100, Expand = ["data.source"] },
                               request, ct))
                transactions.Add(t);

            // A refund or dispute usually names an intent from before the period: list from a day earlier, read the rest.
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
                               request, ct))
                intents[intent.Id] = intent;

            var lines = new List<StripeLedgerLine>();
            foreach (var t in transactions)
            {
                var intentId = t.Source switch
                {
                    Charge c => c.PaymentIntentId,
                    Refund r => r.PaymentIntentId,
                    Dispute d => d.PaymentIntentId,
                    _ => null,
                };
                StripeLedgerPayment? payment = null;
                if (intentId is not null)
                {
                    if (!intents.TryGetValue(intentId, out var intent))
                        intents[intentId] = intent = await new PaymentIntentService(client).GetAsync(intentId, null, request, ct);
                    payment = new StripeLedgerPayment(intent.Id, intent.Description,
                        intent.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal));
                }

                lines.Add(new StripeLedgerLine(t.Id, InstantOf(t.Created), t.ReportingCategory ?? "", t.Amount, t.Fee,
                    t.Description, payment));
            }

            var payouts = new List<StripeLedgerPayout>();
            await foreach (var payout in new PayoutService(client).ListAutoPagingAsync(
                               new PayoutListOptions { Created = period, Limit = 100 }, request, ct))
            {
                long? contentsNet = null;
                var contentsCount = 0;
                // Stripe lists a payout's contents only for automatic payouts, and in full only once it has finished
                // reconciling them; a partial list would read as a mismatch.
                if (payout.Automatic && string.Equals(payout.ReconciliationStatus, "completed", StringComparison.Ordinal))
                {
                    contentsNet = 0;
                    await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                                       new BalanceTransactionListOptions { Payout = payout.Id, Limit = 100 }, request, ct))
                    {
                        if (string.Equals(t.Type, "payout", StringComparison.Ordinal))
                            continue;
                        contentsNet += t.Net;
                        contentsCount++;
                    }
                }

                payouts.Add(new StripeLedgerPayout(payout.Id, InstantOf(payout.Created), InstantOf(payout.ArrivalDate),
                    payout.Amount, payout.Status ?? "", contentsNet, contentsCount));
            }

            return new StripeAccountLedger(lines, payouts);
        }
        catch (StripeException ex)
        {
            throw new InvalidOperationException($"Stripe refused reading the {accountName} account: {ex.Message}", ex);
        }
    }

    public Task<long?> GetAccountPaymentFeeAsync(string accountName, string paymentIntentId, CancellationToken ct = default) =>
        FeeAsync(accountName, paymentIntentId, async (client, request) => await new PaymentIntentService(client).GetAsync(
            paymentIntentId, new PaymentIntentGetOptions { Expand = ["latest_charge.balance_transaction"] }, request, ct), ct);

    public Task<long?> FindAccountPaymentFeeAsync(
        string accountName, IReadOnlyDictionary<string, string> metadata, Instant createdSince, CancellationToken ct = default) =>
        FeeAsync(accountName, string.Join(", ", metadata.Select(m => $"{m.Key}={m.Value}")), async (client, request) =>
        {
            var service = new PaymentIntentService(client);
            var query = string.Join(" AND ", metadata.Select(m => $"metadata['{Quoted(m.Key)}']:'{Quoted(m.Value)}'"));
            var found = (await service.SearchAsync(new PaymentIntentSearchOptions
            {
                Query = $"{query} AND status:'succeeded'",
                Expand = ["data.latest_charge.balance_transaction"],
            }, request, ct)).Data.FirstOrDefault();
            if (found is not null || (clock ?? SystemClock.Instance).GetCurrentInstant() - createdSince > SearchLag)
                return found;

            await foreach (var intent in service.ListAutoPagingAsync(new PaymentIntentListOptions
            {
                Created = new DateRangeOptions { GreaterThanOrEqual = (createdSince - Duration.FromMinutes(1)).ToDateTimeUtc() },
                Limit = 100,
                Expand = ["data.latest_charge.balance_transaction"],
            }, request, ct))
            {
                if (string.Equals(intent.Status, "succeeded", StringComparison.Ordinal)
                    && metadata.All(m => intent.Metadata is not null && intent.Metadata.TryGetValue(m.Key, out var v)
                        && string.Equals(v, m.Value, StringComparison.Ordinal)))
                    return intent;
            }

            return null;
        }, ct);

    private async Task<long?> FeeAsync(
        string accountName, string what, Func<StripeClient, RequestOptions, Task<PaymentIntent?>> read, CancellationToken ct)
    {
        using var _ = logger.TimeOperation();
        if (Reader(accountName) is not var (client, request))
            return null;
        try
        {
            return (await read(client, request))?.LatestCharge?.BalanceTransaction?.Fee;
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not read the Stripe fee of {What} on the {Account} account", what, accountName);
            return null;
        }
    }

    public async Task<IReadOnlyList<StripeAccountCheck>> CheckAccountAsync(string accountName, CancellationToken ct = default)
    {
        var account = Account(accountName);
        if (account is null)
            return [new(false, $"Stripe:MirrorAccounts has no {accountName} entry: its Stripe fees and payouts are not reconciled.")];
        var keyName = account.UsesOrgKey ? StripeMirrorConfiguration.OrgKeyVariable : account.KeyVariable!;
        if (Reader(accountName) is not var (client, request))
            return [new(false, $"{keyName} is unset or contains whitespace: the {accountName} account's Stripe fees and payouts are not reconciled.")];

        var mode = account.ApiKey.StartsWith("sk_test_", StringComparison.Ordinal) || account.ApiKey.StartsWith("rk_test_", StringComparison.Ordinal)
            ? "test mode"
            : "live mode";
        return
        [
            await ProbeAsync(keyName, $"Balance transactions ({mode})",
                () => new BalanceTransactionService(client).ListAsync(new BalanceTransactionListOptions { Limit = 1 }, request, ct)),
            await ProbeAsync(keyName, "Payouts",
                () => new PayoutService(client).ListAsync(new PayoutListOptions { Limit = 1 }, request, ct)),
            await ProbeAsync(keyName, "PaymentIntents",
                () => new PaymentIntentService(client).ListAsync(new PaymentIntentListOptions { Limit = 1 }, request, ct)),
        ];
    }

    private static async Task<StripeAccountCheck> ProbeAsync(string keyName, string what, Func<Task> read)
    {
        try
        {
            await read();
            return new(true, $"{keyName} reads {what}.");
        }
        catch (StripeException ex)
        {
            return new(false, $"Stripe refused {keyName} reading {what} ({(int)ex.HttpStatusCode} {ex.StripeError?.Code ?? ex.StripeError?.Type ?? "error"}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new(false, $"Stripe could not be reached: {ex.Message}");
        }
    }

    private static string Quoted(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static Instant InstantOf(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
