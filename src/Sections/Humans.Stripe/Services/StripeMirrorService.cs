using System.Text.Json;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Base.Interfaces;
using Humans.Stripe.Data;
using Humans.Stripe.Domain;
using Humans.Stripe.Models;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Stripe.Services;

internal enum StripeSyncOutcome { NotConfigured, Busy, Synced, SomeFailed }

/// <summary>The Stripe mirror and the <c>/Stripe</c> pages over it. Pages read the mirror only.</summary>
internal interface IStripeMirrorAdminService : IApplicationService
{
    /// <summary>Sweeps each configured account in turn. A failed account records its error on its sync row and leaves
    /// its mirror as it was; the others still sweep. A sweep already running makes this one return
    /// <see cref="StripeSyncOutcome.Busy"/>.</summary>
    Task<StripeSyncOutcome> SyncAsync(CancellationToken ct = default);

    Task<StripeSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<StripeTransactionsPage> GetTransactionsPageAsync(StripeTransactionsFilter filter, CancellationToken ct = default);
    Task<StripeTransactionView?> GetTransactionViewAsync(string id, CancellationToken ct = default);
    Task<StripePayoutsPage> GetPayoutsPageAsync(string? account, CancellationToken ct = default);
    Task<StripeReservesPage> GetReservesPageAsync(CancellationToken ct = default);
}

internal sealed class StripeMirrorService(
    IStripeMirrorRepository repo,
    IStripeMirrorClient client,
    IOptions<StripeMirrorOptions> options,
    IClock clock,
    IUserService users,
    ILogger<StripeMirrorService> logger) : IStripeMirrorAdminService, IUserDataContributor
{
    /// <summary>One sweep at a time across the nightly job and Sync now; a second caller skips instead of waiting.</summary>
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    private const string EurCode = "EUR";

    private readonly IReadOnlyList<StripeMirrorAccount> _accounts = options.Value.Accounts;

    public async Task<StripeSyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        var configured = _accounts.Where(a => a.IsConfigured).ToList();
        if (configured.Count == 0) return StripeSyncOutcome.NotConfigured;
        if (!await SyncGate.WaitAsync(0, ct)) return StripeSyncOutcome.Busy;
        var failed = 0;
        try
        {
            var states = (await repo.GetSyncStatesAsync(ct)).ToDictionary(s => s.AccountId, StringComparer.Ordinal);
            foreach (var account in configured)
            {
                ct.ThrowIfCancellationRequested();
                var state = states.GetValueOrDefault(account.Id) ?? new StripeSyncState { AccountId = account.Id };
                if (!await SyncAccountAsync(account, state, ct)) failed++;
            }
            return failed == 0 ? StripeSyncOutcome.Synced : StripeSyncOutcome.SomeFailed;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested && failed > 0)
        {
            throw new InvalidOperationException($"Stripe mirror sweep interrupted after {failed} account failure(s).", ex);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    private async Task<bool> SyncAccountAsync(StripeMirrorAccount account, StripeSyncState state, CancellationToken ct)
    {
        var previousError = state.LastError;
        var mirrorReplaced = false;
        state.SyncStatus = StripeSyncStatus.Running;
        state.StatusChangedAt = clock.GetCurrentInstant();
        try
        {
            await repo.SaveSyncStateAsync(state, ct);
            // Everything is fetched before anything is written, so a failed call leaves the mirror as it was.
            var fetch = await client.FetchAsync(account, ct);
            var transactions = FillFromSiblings(fetch.Transactions);
            var now = clock.GetCurrentInstant();
            ct.ThrowIfCancellationRequested();
            await repo.ReplaceAccountAsync(
                new StripeAccountSnapshot(account.Id, transactions, fetch.Payouts, fetch.Balances), now, ct);
            mirrorReplaced = true;

            state.SyncStatus = StripeSyncStatus.Idle;
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastCount = transactions.Count;
            state.LastError = null;
            // The account mirror committed; its outcome must finish despite caller shutdown.
            await repo.SaveSyncStateAsync(state, CancellationToken.None);
            logger.LogInformation("Stripe {Account} sweep mirrored {Transactions} transactions and {Payouts} payouts",
                account.Name, transactions.Count, fetch.Payouts.Count);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !mirrorReplaced)
        {
            await RecordOutcomeAsync(state, StripeSyncStatus.Idle, previousError);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe {Account} sweep failed", account.Name);
            await RecordOutcomeAsync(state, StripeSyncStatus.Error, ex.Message);
            return false;
        }
    }

    private async Task RecordOutcomeAsync(StripeSyncState state, StripeSyncStatus status, string? message)
    {
        try
        {
            state.SyncStatus = status;
            state.StatusChangedAt = clock.GetCurrentInstant();
            state.LastError = message is null ? null : StripeMirrorClient.Clip(message, 2000);
            await repo.SaveSyncStateAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Stripe sync outcome for account {AccountId}", state.AccountId);
        }
    }

    /// <summary>A refund or dispute names no buyer; it takes one from a row on the same PaymentIntent, so searching a
    /// name finds the whole story.</summary>
    internal static List<StripeBalanceTransaction> FillFromSiblings(IReadOnlyList<StripeBalanceTransaction> rows)
    {
        var byIntent = rows
            .Where(t => !string.IsNullOrWhiteSpace(t.PaymentIntentId))
            .GroupBy(t => t.PaymentIntentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (
                Name: g.Select(t => t.CustomerName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                Email: g.Select(t => t.CustomerEmail).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))),
                StringComparer.Ordinal);
        foreach (var t in rows.Where(t => !string.IsNullOrWhiteSpace(t.PaymentIntentId)))
        {
            if (!byIntent.TryGetValue(t.PaymentIntentId!, out var sibling)) continue;
            if (string.IsNullOrWhiteSpace(t.CustomerName)) t.CustomerName = sibling.Name;
            if (string.IsNullOrWhiteSpace(t.CustomerEmail)) t.CustomerEmail = sibling.Email;
        }
        return rows.DistinctBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    private static decimal Eur(long minors) => minors / 100m;

    private string NameOf(string accountId) =>
        _accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal))?.Name ?? accountId;

    /// <summary>Configured accounts first, in config order, then any account still mirrored but no longer configured.</summary>
    private List<(string Id, string Name)> AccountList(IEnumerable<string> mirroredIds) =>
        _accounts.Select(a => (a.Id, a.Name))
            .Concat(mirroredIds.Distinct(StringComparer.Ordinal)
                .Where(id => !_accounts.Any(a => string.Equals(a.Id, id, StringComparison.Ordinal)))
                .Select(id => (id, id)))
            .ToList();

    private static bool Is(StripeBalanceTransaction t, string category) =>
        string.Equals(t.ReportingCategory, category, StringComparison.Ordinal);

    public async Task<StripeSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var transactions = await repo.GetTransactionsAsync(ct);
        var payouts = await repo.GetPayoutsAsync(ct);
        var balances = await repo.GetBalancesAsync(ct);
        var states = await repo.GetSyncStatesAsync(ct);
        var today = clock.GetCurrentInstant().InUtc().Date;

        var byAccount = transactions.ToLookup(t => t.AccountId, StringComparer.Ordinal);
        var summaries = AccountList(transactions.Select(t => t.AccountId)).Select(a =>
        {
            var config = _accounts.FirstOrDefault(c => string.Equals(c.Id, a.Id, StringComparison.Ordinal));
            var all = byAccount[a.Id].ToList();
            var eur = all.Where(t => string.Equals(t.Currency, EurCode, StringComparison.Ordinal)).ToList();
            var accountBalances = balances.Where(b => string.Equals(b.AccountId, a.Id, StringComparison.Ordinal)).ToList();
            var eurBalance = accountBalances.FirstOrDefault(b => string.Equals(b.Currency, EurCode, StringComparison.Ordinal));
            var computed = Eur(eur.Sum(t => t.Net));
            var state = states.FirstOrDefault(s => string.Equals(s.AccountId, a.Id, StringComparison.Ordinal));

            var reserve = StripeLedger.HeldOverTime(eur.Where(StripeLedger.IsRiskReserve));
            var lastMinimum = eur.Where(StripeLedger.IsMinimumBalanceHold).MaxBy(t => t.Created);
            var peak = reserve.Count == 0 ? null : reserve.MaxBy(p => p.Held);

            return new StripeAccountSummary(
                a.Id,
                a.Name,
                config?.IsConfigured ?? false,
                config?.UsesOrgKey ?? true,
                state is null ? null : new StripeSyncInfo(state.SyncStatus.ToString(), state.LastSyncAt, state.LastError),
                all.Count,
                payouts.Count(p => string.Equals(p.AccountId, a.Id, StringComparison.Ordinal)),
                accountBalances.Select(b => new StripeMoney(b.Currency, Eur(b.Available), Eur(b.Pending))).ToList(),
                computed,
                eurBalance is null ? null : Eur(eurBalance.Available + eurBalance.Pending) - computed,
                Eur(eur.Where(t => Is(t, "charge")).Sum(t => t.Amount)),
                Eur(eur.Sum(t => t.Fee) - eur.Where(t => Is(t, "fee")).Sum(t => t.Amount)),
                Eur(eur.Where(t => Is(t, "refund")).Sum(t => t.Amount)),
                -Eur(eur.Where(t => Is(t, "payout") || Is(t, "payout_reversal")).Sum(t => t.Net)),
                Eur(StripeLedger.HeldOn(reserve, today)),
                peak is null || peak.Held <= 0 ? null : new StripeHeldPoint(peak.Date, Eur(peak.Held), 0m),
                lastMinimum is null ? null
                    : new StripeHeldPoint(lastMinimum.Created.InUtc().Date, -Eur(lastMinimum.Amount), 0m),
                all.Select(t => t.Currency).Concat(accountBalances.Select(b => b.Currency))
                    .Where(c => !string.Equals(c, EurCode, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToList());
        }).ToList();

        return new StripeSummary(_accounts.Any(a => a.IsConfigured), summaries);
    }

    public async Task<StripeTransactionsPage> GetTransactionsPageAsync(
        StripeTransactionsFilter filter, CancellationToken ct = default)
    {
        var all = await repo.GetTransactionsAsync(ct);
        IEnumerable<StripeBalanceTransaction> rows = all;
        if (!string.IsNullOrEmpty(filter.Account))
            rows = rows.Where(t => string.Equals(t.AccountId, filter.Account, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(filter.Type))
            rows = rows.Where(t => string.Equals(t.Type, filter.Type, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(filter.Payout))
            rows = rows.Where(t => string.Equals(t.PayoutId, filter.Payout, StringComparison.Ordinal));
        if (filter.Q?.Trim() is { Length: > 0 } q)
            rows = rows.Where(t => Matches(t, q));

        return new StripeTransactionsPage(
            filter,
            rows.Select(ToRow).ToList(),
            AccountList(all.Select(t => t.AccountId)).Select(a => new StripeOption(a.Id, a.Name)).ToList(),
            all.Select(t => t.Type).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Ids, description, buyer and the source charge's metadata (order ids), case-insensitively.</summary>
    internal static bool Matches(StripeBalanceTransaction t, string q) =>
        new[] { t.Id, t.SourceId, t.PaymentIntentId, t.PayoutId, t.Description, t.CustomerName, t.CustomerEmail, t.Metadata }
            .Any(field => field?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    private StripeTransactionRow ToRow(StripeBalanceTransaction t) => new(
        t.Id, NameOf(t.AccountId), t.Created, StripeLedger.AvailableDate(t), t.Type, t.ReportingCategory, t.Status,
        Eur(t.Amount), Eur(t.Fee), Eur(t.Net), t.Currency, t.Description, t.CustomerName, t.CustomerEmail, t.SourceId,
        t.PayoutId);

    public async Task<StripeTransactionView?> GetTransactionViewAsync(string id, CancellationToken ct = default)
    {
        var t = await repo.GetTransactionAsync(id, ct);
        if (t is null) return null;
        return new StripeTransactionView(ToRow(t), t.AccountId, t.PaymentIntentId, t.Metadata is null ? null : Pretty(t.Metadata),
            Pretty(t.Raw));
    }

    private static string Pretty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<StripePayoutsPage> GetPayoutsPageAsync(string? account, CancellationToken ct = default)
    {
        var payouts = await repo.GetPayoutsAsync(ct);
        var transactions = await repo.GetTransactionsAsync(ct);
        // A payout's own row carries its id too; only the rows it paid out count as linked.
        var linked = transactions
            .Where(t => t.PayoutId is not null && !Is(t, "payout"))
            .ToLookup(t => t.PayoutId!, StringComparer.Ordinal);

        var rows = payouts
            .Where(p => string.IsNullOrEmpty(account) || string.Equals(p.AccountId, account, StringComparison.Ordinal))
            .Select(p => new StripePayoutRow(p.Id, NameOf(p.AccountId), p.Created, p.ArrivalDate.InUtc().Date,
                Eur(p.Amount), p.Currency, p.Status, p.Automatic, p.Method, p.FailureMessage,
                linked[p.Id].Count(), Eur(linked[p.Id].Sum(t => t.Net))))
            .ToList();

        return new StripePayoutsPage(account, rows,
            AccountList(payouts.Select(p => p.AccountId)).Select(a => new StripeOption(a.Id, a.Name)).ToList());
    }

    public async Task<StripeReservesPage> GetReservesPageAsync(CancellationToken ct = default)
    {
        var transactions = (await repo.GetTransactionsAsync(ct))
            .Where(t => string.Equals(t.Currency, EurCode, StringComparison.Ordinal)).ToList();
        var curves = new List<StripeHoldCurve>();
        foreach (var (id, name) in AccountList(transactions.Select(t => t.AccountId)))
        {
            var account = transactions.Where(t => string.Equals(t.AccountId, id, StringComparison.Ordinal)).ToList();
            var rows = account.Where(StripeLedger.IsRiskReserve).ToList();
            if (rows.Count == 0) continue;
            long previous = 0;
            var shown = StripeLedger.HeldOverTime(rows).Select(p =>
            {
                var point = new StripeHeldPoint(p.Date, Eur(p.Held), Eur(p.Held - previous));
                previous = p.Held;
                return point;
            }).ToList();
            curves.Add(new StripeHoldCurve(name, "Risk reserve", shown, shown.Max(p => p.Held)));
        }
        return new StripeReservesPage(curves);
    }

    // ── GDPR ─────────────────────────────────────────────────────────────────

    internal const string PaymentsSlice = "StripePayments";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [PaymentsSlice] =
                "Retained in full, nothing erased: each row is a Stripe balance transaction (a payment, refund or " +
                "dispute) with the buyer's name and email as Stripe recorded them. Spanish law requires the books and " +
                "their supporting documents be kept 6 years (Código de Comercio Art. 30) and 4 years for tax purposes " +
                "(Ley 58/2003 Art. 66), and Stripe keeps the same records regardless. The rows carry no user id. " +
                "GDPR Art. 17(3)(b).",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Rows whose buyer email is one of the member's verified addresses. An unverified address could be
    /// someone else's, so it never pulls their payments into this export.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var emails = (info?.UserEmails ?? []).Where(e => e.IsVerified)
            .Select(e => e.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (emails.Count == 0) return [];

        var rows = (await repo.GetTransactionsAsync(ct))
            .Where(t => t.CustomerEmail is not null && emails.Contains(t.CustomerEmail))
            .OrderBy(t => t.Created)
            .Select(t => new
            {
                Account = NameOf(t.AccountId),
                t.Id,
                t.Type,
                Created = t.Created.ToIso8601(),
                Amount = Eur(t.Amount),
                t.Currency,
                t.Description,
                t.CustomerName,
                t.CustomerEmail,
            })
            .ToList();
        return rows.Count == 0 ? [] : [new UserDataSlice(PaymentsSlice, rows)];
    }

    /// <summary>Financial records are never erased (see <see cref="Erasure"/>); there is no member binding to drop.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
