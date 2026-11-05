using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Stripe.Domain;
using NodaTime;
// `Stripe` would bind to this section inside its namespaces; see StripeService.cs.
using Stripe;

namespace Humans.Stripe.Services;

/// <summary>One Stripe account the mirror reads. Ids are not secret; keys come from env vars only.</summary>
internal sealed class StripeMirrorAccount
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";

    /// <summary>The env var holding this account's own read key. Unset: the org key with <c>Stripe-Context</c>.</summary>
    public string? KeyVariable { get; set; }

    /// <summary>Resolved at startup from <see cref="KeyVariable"/>, or the org key.</summary>
    public string ApiKey { get; set; } = "";

    public bool UsesOrgKey => string.IsNullOrEmpty(KeyVariable);
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
}

internal sealed class StripeMirrorOptions
{
    public List<StripeMirrorAccount> Accounts { get; set; } = [];
}

/// <summary>Read-only: list and retrieve calls only, whatever scopes the key holds.</summary>
internal interface IStripeMirrorClient
{
    /// <summary>Every balance transaction (sources expanded for search, automatic payouts linked), every payout and the
    /// balance. Throws on any failure or a pager that cannot be trusted: the sweep deletes what a fetch leaves out.</summary>
    Task<StripeAccountFetch> FetchAsync(StripeMirrorAccount account, CancellationToken ct = default);
}

internal sealed record StripeAccountFetch(
    IReadOnlyList<StripeBalanceTransaction> Transactions,
    IReadOnlyList<StripePayout> Payouts,
    IReadOnlyList<StripeBalance> Balances);

internal sealed class StripeMirrorClient(IHttpClient? httpClient = null) : IStripeMirrorClient
{
    private const int PageLimit = 100;

    /// <summary>Throws rather than returning a prefix. 500 pages is 50,000 rows, ten times Tickets 2026.</summary>
    private const int PageSafetyCap = 500;

    public async Task<StripeAccountFetch> FetchAsync(StripeMirrorAccount account, CancellationToken ct = default)
    {
        var client = new StripeClient(account.ApiKey, httpClient: httpClient);
        // The org key reaches each account through Stripe-Context; an account's own key needs none. The SDK always
        // sends its pinned Stripe-Version (ApiVersion.Current), which an org key requires.
        RequestOptions Request() => account.UsesOrgKey ? new RequestOptions { StripeContext = account.Id } : new RequestOptions();

        var transactionService = new BalanceTransactionService(client);
        var listed = await AllPagesAsync(after =>
        {
            var options = new BalanceTransactionListOptions { Limit = PageLimit, StartingAfter = after };
            options.AddExpand("data.source");
            return transactionService.ListAsync(options, Request(), ct);
        }, t => t.Id, "balance transactions");

        var payouts = await AllPagesAsync(
            after => new PayoutService(client).ListAsync(new PayoutListOptions { Limit = PageLimit, StartingAfter = after }, Request(), ct),
            p => p.Id, "payouts");

        // Stripe links transactions only to reconcilable automatic payouts; asking for a split payout's secondary part
        // (reconciliation_status not_applicable) is a 400. A paid payout wins over a failed one listing the same row.
        var payoutOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var payout in payouts
                     .Where(p => p.Automatic && !string.Equals(p.ReconciliationStatus, "not_applicable", StringComparison.Ordinal))
                     .OrderBy(p => string.Equals(p.Status, "paid", StringComparison.Ordinal)))
        {
            var ids = await AllPagesAsync(after => transactionService.ListAsync(
                new BalanceTransactionListOptions { Payout = payout.Id, Limit = PageLimit, StartingAfter = after }, Request(), ct),
                t => t.Id, $"payout {payout.Id} transactions");
            foreach (var t in ids) payoutOf[t.Id] = payout.Id;
        }

        var balance = await new BalanceService(client).GetAsync(Request(), ct);

        return new StripeAccountFetch(
            listed.Select(t => ToTransaction(account.Id, t, payoutOf.GetValueOrDefault(t.Id))).ToList(),
            payouts.Select(p => ToPayout(account.Id, p)).ToList(),
            ToBalances(account.Id, balance));
    }

    private static async Task<List<T>> AllPagesAsync<T>(
        Func<string?, Task<StripeList<T>>> page, Func<T, string> id, string what)
    {
        var items = new List<T>();
        string? after = null;
        for (var n = 1; n <= PageSafetyCap; n++)
        {
            var list = await page(after);
            if (list?.Data is null) throw new InvalidOperationException($"Stripe {what}: a page without data.");
            items.AddRange(list.Data);
            if (!list.HasMore) return items;
            if (list.Data.Count == 0) throw new InvalidOperationException($"Stripe {what}: has_more on an empty page.");
            after = id(list.Data[^1]);
        }
        throw new InvalidOperationException($"Stripe {what}: hit the {PageSafetyCap}-page safety cap.");
    }

    internal static StripeBalanceTransaction ToTransaction(string accountId, BalanceTransaction t, string? payoutId)
    {
        var (paymentIntentId, name, email, metadata) = t.Source switch
        {
            Charge c => (c.PaymentIntentId,
                c.BillingDetails?.Name ?? Meta(c.Metadata, "name"),
                c.BillingDetails?.Email ?? c.ReceiptEmail ?? Meta(c.Metadata, "email_address") ?? Meta(c.Metadata, "email"),
                c.Metadata),
            Refund r => (r.PaymentIntentId, null, null, r.Metadata),
            Dispute d => (d.PaymentIntentId, null, null, d.Metadata),
            _ => ((string?)null, (string?)null, (string?)null, (Dictionary<string, string>?)null),
        };

        return new StripeBalanceTransaction
        {
            Id = t.Id,
            AccountId = accountId,
            Type = t.Type ?? "",
            ReportingCategory = t.ReportingCategory ?? "",
            Status = t.Status ?? "",
            Currency = (t.Currency ?? "").ToUpperInvariant(),
            Amount = t.Amount,
            Fee = t.Fee,
            Net = t.Net,
            Created = Instant.FromDateTimeUtc(DateTime.SpecifyKind(t.Created, DateTimeKind.Utc)),
            AvailableOn = Instant.FromDateTimeUtc(DateTime.SpecifyKind(t.AvailableOn, DateTimeKind.Utc)),
            Description = Clip(t.Description, 1000),
            SourceId = t.SourceId,
            PayoutId = string.Equals(t.Type, "payout", StringComparison.Ordinal) ? t.SourceId : payoutId,
            PaymentIntentId = paymentIntentId,
            CustomerName = Clip(name, 256),
            CustomerEmail = Clip(email, 256),
            Metadata = metadata is { Count: > 0 } ? JsonSerializer.Serialize(metadata) : null,
            Raw = RawWithSourceId(t),
        };
    }

    /// <summary>The expanded charge carries card and address details the mirror has no use for; only its id is kept.</summary>
    private static string RawWithSourceId(BalanceTransaction t)
    {
        var element = t.RawJsonElement;
        var raw = JsonNode.Parse(element is { ValueKind: JsonValueKind.Object } e ? e.GetRawText() : t.ToJson())!.AsObject();
        if (raw["source"] is JsonObject) raw["source"] = t.SourceId;
        return raw.ToJsonString();
    }

    private static string? Meta(Dictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static string? Clip(string? s, int max) => s is { Length: var n } && n > max ? s[..max] : s;

    private static StripePayout ToPayout(string accountId, Payout p) => new()
    {
        Id = p.Id,
        AccountId = accountId,
        Amount = p.Amount,
        Currency = (p.Currency ?? "").ToUpperInvariant(),
        Status = p.Status ?? "",
        Automatic = p.Automatic,
        Method = p.Method,
        Created = Instant.FromDateTimeUtc(DateTime.SpecifyKind(p.Created, DateTimeKind.Utc)),
        ArrivalDate = Instant.FromDateTimeUtc(DateTime.SpecifyKind(p.ArrivalDate, DateTimeKind.Utc)),
        BalanceTransactionId = p.BalanceTransactionId,
        FailureCode = p.FailureCode,
        FailureMessage = Clip(p.FailureMessage, 1000),
        StatementDescriptor = p.StatementDescriptor,
    };

    private static List<StripeBalance> ToBalances(string accountId, Balance balance)
    {
        var rows = new Dictionary<string, StripeBalance>(StringComparer.Ordinal);
        StripeBalance Row(string currency)
        {
            var key = currency.ToUpperInvariant();
            if (!rows.TryGetValue(key, out var row))
                rows[key] = row = new StripeBalance { AccountId = accountId, Currency = key };
            return row;
        }
        foreach (var a in balance.Available ?? []) Row(a.Currency).Available += a.Amount;
        foreach (var p in balance.Pending ?? []) Row(p.Currency).Pending += p.Amount;
        return rows.Values.ToList();
    }
}

/// <summary>Binds <c>Stripe:MirrorAccounts</c> and resolves each account's key from the environment.</summary>
internal static class StripeMirrorConfiguration
{
    public const string OrgKeyVariable = "STRIPE_ORG_READ_KEY";
    public const string AccountsKey = "Stripe:MirrorAccounts";

    public static void Bind(StripeMirrorOptions options, IEnumerable<StripeMirrorAccount> configured)
    {
        var orgKey = Environment.GetEnvironmentVariable(OrgKeyVariable) ?? "";
        foreach (var account in configured.Where(a => !string.IsNullOrWhiteSpace(a.Id)))
        {
            account.ApiKey = account.UsesOrgKey ? orgKey : Environment.GetEnvironmentVariable(account.KeyVariable!) ?? "";
            options.Accounts.Add(account);
        }
    }
}
