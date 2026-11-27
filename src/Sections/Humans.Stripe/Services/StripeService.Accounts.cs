using Humans.Base.Extensions;
using Humans.Stripe.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;
// See StripeService.cs: the bare name `Stripe` binds to this section here.
using Stripe;
using Stripe.Checkout;

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
            var transactionService = new BalanceTransactionService(client);
            var transactions = await StripeMirrorClient.AllPagesAsync(
                after => transactionService.ListAsync(new BalanceTransactionListOptions
                { Created = period, Limit = 100, StartingAfter = after, Expand = ["data.source"] }, request, ct),
                transaction => transaction.Id, $"{accountName} balance transactions", ct,
                item => StripeMirrorClient.RequireFields(item, "ledger movement", ["reporting_category"], ["amount", "fee", "created"]));
            foreach (var transaction in transactions)
                RequireEur(transaction.Currency, $"balance transaction {transaction.Id}");

            // A refund or dispute usually names an intent from before the period: list from a day earlier, read the rest.
            var intents = (await StripeMirrorClient.AllPagesAsync(
                after => new PaymentIntentService(client).ListAsync(new PaymentIntentListOptions
                {
                    Created = new DateRangeOptions
                    {
                        GreaterThanOrEqual = (from - Duration.FromDays(1)).ToDateTimeUtc(),
                        LessThan = to.ToDateTimeUtc(),
                    },
                    Limit = 100,
                    StartingAfter = after,
                }, request, ct), intent => intent.Id, $"{accountName} payment intents", ct))
                .ToDictionary(intent => intent.Id, StringComparer.Ordinal);

            var lines = new List<StripeLedgerLine>();
            foreach (var t in transactions)
            {
                var (intentId, sourceMetadata) = t.Source switch
                {
                    Charge c => (c.PaymentIntentId, c.Metadata),
                    Refund r => (r.PaymentIntentId, r.Metadata),
                    Dispute d => (d.PaymentIntentId, d.Metadata),
                    _ => (null, null),
                };
                StripeLedgerPayment? payment = null;
                if (intentId is not null)
                {
                    if (!intents.TryGetValue(intentId, out var intent))
                        intents[intentId] = intent = await new PaymentIntentService(client).GetAsync(intentId, null, request, ct);
                    var metadata = new Dictionary<string, string>(intent.Metadata ?? [], StringComparer.Ordinal);
                    // A movement can carry its order reference even when the older intent does not.
                    if (sourceMetadata is not null)
                        foreach (var pair in sourceMetadata)
                            metadata[pair.Key] = pair.Value;
                    payment = new StripeLedgerPayment(intent.Id, intent.Description, metadata);
                }

                lines.Add(new StripeLedgerLine(t.Id, InstantOf(t.Created), t.ReportingCategory ?? "", t.Amount, t.Fee,
                    t.Description, payment));
            }

            var payouts = new List<StripeLedgerPayout>();
            foreach (var payout in await StripeMirrorClient.AllPagesAsync(
                         after => new PayoutService(client).ListAsync(new PayoutListOptions
                         { Created = period, Limit = 100, StartingAfter = after }, request, ct),
                         payout => payout.Id, $"{accountName} payouts", ct,
                         item => StripeMirrorClient.RequireFields(item, "ledger payout", ["status", "reconciliation_status"],
                             ["amount", "created", "arrival_date"], ["automatic"])))
            {
                RequireEur(payout.Currency, $"payout {payout.Id}");
                long? contentsNet = null;
                var contentsCount = 0;
                // Stripe lists a payout's contents only for automatic payouts, and in full only once it has finished
                // reconciling them; a partial list would read as a mismatch.
                if (payout.Automatic && string.Equals(payout.ReconciliationStatus, "completed", StringComparison.Ordinal))
                {
                    contentsNet = 0;
                    foreach (var t in await StripeMirrorClient.AllPagesAsync(
                                 after => transactionService.ListAsync(new BalanceTransactionListOptions
                                 { Payout = payout.Id, Limit = 100, StartingAfter = after }, request, ct),
                                 transaction => transaction.Id, $"payout {payout.Id} contents", ct, item =>
                                 {
                                     StripeMirrorClient.RequireFields(item, "payout movement", ["type"], []);
                                     if (!string.Equals(item.GetProperty("type").GetString(), "payout", StringComparison.Ordinal))
                                         StripeMirrorClient.RequireFields(item, "payout movement", [], ["net"]);
                                 }))
                    {
                        if (string.Equals(t.Type, "payout", StringComparison.Ordinal))
                            continue;
                        RequireEur(t.Currency, $"payout movement {t.Id}");
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
            var transaction = (await read(client, request))?.LatestCharge?.BalanceTransaction;
            if (transaction is null)
                return null;
            if (!string.Equals(transaction.Currency, "eur", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Stripe fee of {What} on {Account} has currency {Currency}; EUR is required",
                    what, accountName, transaction.Currency);
                return null;
            }
            try
            {
                StripeMirrorClient.RequireFields(transaction.RawJsonElement ?? default, "payment fee", [], ["fee"]);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Stripe fee of {What} on the {Account} account has incomplete data", what, accountName);
                return null;
            }
            return transaction.Fee;
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not read the Stripe fee of {What} on the {Account} account", what, accountName);
            return null;
        }
    }

    public async Task<IReadOnlyList<StripeAccountCheck>> CheckAccountAsync(
        string accountName, CancellationToken ct = default, bool includeCheckout = false)
    {
        var findings = new List<StripeAccountCheck>();
        if (includeCheckout)
        {
            var checkoutMode = _settings.TicketsKey.StartsWith("sk_test_", StringComparison.Ordinal)
                || _settings.TicketsKey.StartsWith("rk_test_", StringComparison.Ordinal) ? "test mode" : "live mode";
            findings.Add(_settings.IsConfigured
                ? await ProbeAsync("STRIPE_TICKETS_KEY", $"Checkout Sessions ({checkoutMode})",
                    () => new SessionService(new StripeClient(_settings.TicketsKey, httpClient: httpClient))
                        .ListAsync(new SessionListOptions { Limit = 1 }, cancellationToken: ct), ct)
                : new(false, "STRIPE_TICKETS_KEY is unset or contains whitespace: checkout cannot be read."));
        }
        var account = Account(accountName);
        if (account is null)
        {
            findings.Add(new(false, $"Stripe:MirrorAccounts has no {accountName} entry: its Stripe fees and payouts are not reconciled."));
            return findings;
        }
        var keyName = account.UsesOrgKey ? StripeMirrorConfiguration.OrgKeyVariable : account.KeyVariable!;
        if (Reader(accountName) is not var (client, request))
        {
            findings.Add(new(false, $"{keyName} is unset or contains whitespace: the {accountName} account's Stripe fees and payouts are not reconciled."));
            return findings;
        }

        var mode = account.ApiKey.StartsWith("sk_test_", StringComparison.Ordinal) || account.ApiKey.StartsWith("rk_test_", StringComparison.Ordinal)
            ? "test mode"
            : "live mode";
        findings.AddRange(
        [
            await ProbeAsync(keyName, $"Balance transactions ({mode})",
                () => new BalanceTransactionService(client).ListAsync(new BalanceTransactionListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "Payouts",
                () => new PayoutService(client).ListAsync(new PayoutListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "PaymentIntents",
                () => new PaymentIntentService(client).ListAsync(new PaymentIntentListOptions { Limit = 1 }, request, ct), ct),
        ]);
        return findings;
    }

    private static async Task<StripeAccountCheck> ProbeAsync(string keyName, string what, Func<Task> read, CancellationToken ct)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new(false, $"Stripe could not be reached: {ex.Message}");
        }
    }

    private static void RequireEur(string? currency, string source)
    {
        if (!string.Equals(currency, "eur", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Stripe {source} does not declare EUR currency.");
    }

    private static string Quoted(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static Instant InstantOf(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
