using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance.Dtos;
using Humans.Domain.Entities;
using System.Text.Json;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Application-layer service for the Holded finance integration.
/// Manages account provisioning, purchase-doc sync, actuals computation, and unmatched reporting.
/// </summary>
public sealed class HoldedFinanceService(
    IHoldedRepository repo,
    IHoldedClient client,
    // Cross-section read via full IBudgetService matches existing FinanceController usage.
    // Future: narrow to an IBudgetServiceRead via the section read/write split.
    IBudgetService budget,
    IClock clock,
    IMemoryCache cache,
    ILogger<HoldedFinanceService> logger) : IHoldedFinanceService, IUserDataContributor
{
    private const int SyncPageSafetyCap = 200;
    private static readonly TimeSpan ContactsCacheDuration = TimeSpan.FromMinutes(2);

    // ─── Provisioning ───────────────────────────────────────────────────────────

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var year = await budget.GetActiveYearAsync();
        var categories = year is null
            ? Array.Empty<(Guid Id, string Name, string Group)>()
            : year.Groups
                  .SelectMany(g => g.Categories.Select(c => (c.Id, c.Name, Group: g.Name)))
                  .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map
            .Where(m => m.IsActive)
            .ToDictionary(m => m.BudgetCategoryId);

        // Seed collision avoidance from BOTH the local map and the live Holded chart of
        // accounts, so a number occupied remotely but missing locally — e.g. an account
        // created in Holded whose local map write later failed, or accounts created
        // directly in Holded — is never re-proposed.
        var remoteAccounts = await client.ListExpenseAccountsAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.AccountNum))
            .ToHashSet();

        var rows = new List<HoldedProvisioningRow>();
        var usedTags = new HashSet<string>(StringComparer.Ordinal);
        var currentActiveCatIds = categories.Select(c => c.Id).ToHashSet();

        // Track the rolling "next free" number across ToAdd assignments.
        int nextFree = blockStart;

        // Walk categories in stable order: group then category name.
        foreach (var (catId, catName, groupName) in categories
            .OrderBy(c => c.Group, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            if (activeByCat.TryGetValue(catId, out var existing))
            {
                rows.Add(new HoldedProvisioningRow(
                    BudgetCategoryId: catId,
                    CategoryName: catName,
                    GroupName: groupName,
                    ExistingAccountNum: existing.HoldedAccountNumber,
                    ProposedAccountNum: null,
                    Tag: existing.Tag,
                    State: "Mapped"));
                usedTags.Add(existing.Tag);
            }
            else
            {
                var tag = UniqueTag(groupName, catName, catId, usedTags);
                usedTags.Add(tag);

                // Advance nextFree past any already-used numbers.
                while (usedNumbers.Contains(nextFree))
                    nextFree++;
                var proposed = nextFree;
                usedNumbers.Add(proposed);
                nextFree++;

                rows.Add(new HoldedProvisioningRow(
                    BudgetCategoryId: catId,
                    CategoryName: catName,
                    GroupName: groupName,
                    ExistingAccountNum: null,
                    ProposedAccountNum: proposed,
                    Tag: tag,
                    State: "ToAdd"));
            }
        }

        // Orphans: active map rows whose category no longer exists.
        foreach (var m in activeByCat.Values.Where(m => !currentActiveCatIds.Contains(m.BudgetCategoryId)))
        {
            rows.Add(new HoldedProvisioningRow(
                BudgetCategoryId: m.BudgetCategoryId,
                CategoryName: "(deleted)",
                GroupName: "(deleted)",
                ExistingAccountNum: m.HoldedAccountNumber,
                ProposedAccountNum: null,
                Tag: m.Tag,
                State: "Orphan"));
        }

        // Final nextFree after all assignments.
        while (usedNumbers.Contains(nextFree))
            nextFree++;

        return new HoldedProvisioningPlan(rows, nextFree);
    }

    public async Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default)
    {
        var plan = await GetProvisioningPlanAsync(blockStart, ct);
        var toAdd = plan.Rows.Where(r => string.Equals(r.State, "ToAdd", StringComparison.Ordinal)).ToList();
        if (!addAll)
            toAdd = toAdd.Take(1).ToList();

        var now = clock.GetCurrentInstant();
        var created = 0;

        foreach (var row in toAdd)
        {
            try
            {
                var accountName = $"{row.GroupName} / {row.CategoryName}";
                var id = await client.CreateExpenseAccountAsync(row.ProposedAccountNum!.Value, accountName, ct);
                await repo.AddCategoryMapAsync(new HoldedCategoryMap
                {
                    Id = Guid.NewGuid(),
                    BudgetCategoryId = row.BudgetCategoryId,
                    HoldedAccountNumber = row.ProposedAccountNum.Value,
                    HoldedAccountId = id,
                    Tag = row.Tag,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct);
                created++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to provision Holded account for category {CategoryId} ({Name})",
                    row.BudgetCategoryId, row.CategoryName);
                // Partial success: already-created rows are persisted; let this one propagate.
                throw;
            }
        }

        return created;
    }

    // ─── Sync ────────────────────────────────────────────────────────────────────

    public async Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var state = await repo.GetSyncStateAsync(ct);
        state.SyncStatus = HoldedSyncStatus.Running;
        state.StatusChangedAt = now;
        await repo.SaveSyncStateAsync(state, ct);

        try
        {
            var map = await repo.GetCategoryMapAsync(ct);
            var entries = map
                .Where(m => m.IsActive)
                .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.HoldedAccountNumber, m.Tag))
                .ToArray();

            // Page through all purchase documents.
            var allDocs = new List<HoldedPurchaseDocListItemDto>();
            for (var page = 1; page <= SyncPageSafetyCap; page++)
            {
                var pageDocs = await client.ListPurchaseDocumentsPageAsync(page, 100, ct);
                if (pageDocs.Count == 0)
                    break;

                allDocs.AddRange(pageDocs);

                if (page == SyncPageSafetyCap)
                {
                    logger.LogWarning(
                        "HoldedFinanceService.SyncAsync: safety cap of {Cap} pages reached — some docs may be missing",
                        SyncPageSafetyCap);
                }
            }

            var docs = allDocs.Select(doc => MapDoc(doc, entries, now)).ToList();

            await repo.UpsertDocsAsync(docs, now, ct);

            var matched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
            var unmatched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Unmatched);

            state.SyncStatus = HoldedSyncStatus.Idle;
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastError = null;
            state.LastSyncedDocCount = docs.Count;
            await repo.SaveSyncStateAsync(state, ct);

            return new HoldedSyncResult(docs.Count, matched, unmatched);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HoldedFinanceService.SyncAsync failed");
            state.SyncStatus = HoldedSyncStatus.Error;
            state.LastError = ex.Message;
            state.StatusChangedAt = now;
            try { await repo.SaveSyncStateAsync(state, CancellationToken.None); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist error sync state"); }
            throw;
        }
    }

    private static HoldedExpenseDoc MapDoc(
        HoldedPurchaseDocListItemDto doc,
        HoldedMatchEntry[] entries,
        Instant now)
    {
        // v1 attributes the whole doc by its FIRST line's account (+ union of doc/line tags)
        // and assigns the full doc.Total to that one category. Per spec §6, virtually all
        // purchase docs today are single-line, so this is correct in practice. Line-level
        // attribution (splitting a mixed-account doc's total across categories) is a
        // deliberate later refinement, not a v1 requirement.
        var bookedAccount = doc.Lines.Count > 0 ? doc.Lines[0].AccountId : null;
        var tags = doc.Tags
            .Concat(doc.Lines.SelectMany(l => l.Tags))
            .ToList();

        var matchResult = HoldedMatcher.Match(bookedAccount, tags, entries);

        var localDate = doc.Date
            .InZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"])
            .Date;

        return new HoldedExpenseDoc
        {
            Id = Guid.NewGuid(),
            HoldedDocId = doc.Id,
            DocNumber = doc.DocNumber,
            ContactName = doc.ContactName,
            Date = localDate,
            Subtotal = doc.Subtotal,
            Tax = doc.Tax,
            Total = doc.Total,
            Currency = doc.Currency,
            ApprovedAt = doc.ApprovedAt,
            TagsJson = System.Text.Json.JsonSerializer.Serialize(tags),
            BookedAccountId = bookedAccount,
            BudgetCategoryId = matchResult.CategoryId,
            MatchStatus = matchResult.CategoryId is null
                ? HoldedMatchStatus.Unmatched
                : HoldedMatchStatus.Matched,
            MatchSource = matchResult.Source,
            RawPayload = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ─── Actuals ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default)
    {
        var docs = await repo.GetMatchedForYearAsync(calendarYear, ct);
        return docs
            .Where(d => d.ApprovedAt is not null && d.BudgetCategoryId is not null)
            .GroupBy(d => d.BudgetCategoryId!.Value)
            .Select(g => new HoldedActualRow(g.Key, g.Sum(x => x.Total), g.Count()))
            .ToList();
    }

    // ─── Unmatched ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var docs = await repo.GetUnmatchedAsync(ct);
        return docs
            .Select(d => new HoldedUnmatchedRow(
                d.HoldedDocId,
                d.DocNumber,
                d.ContactName,
                d.Total,
                ReasonFor(d),
                // TODO(probe): confirm Holded deep-link URL format
                $"https://app.holded.com/purchases/{d.HoldedDocId}"))
            .ToList();
    }

    private static string ReasonFor(HoldedExpenseDoc d)
    {
        var hasAccount = !string.IsNullOrEmpty(d.BookedAccountId);
        // Tags are stored as JSON; a non-empty array means at least one tag existed.
        var hasTags = d.TagsJson is not null
            && !string.Equals(d.TagsJson, "[]", StringComparison.Ordinal)
            && !string.Equals(d.TagsJson, "null", StringComparison.Ordinal);

        if (!hasAccount && !hasTags)
            return "No account, no tag";
        if (hasAccount && hasTags)
            return "Account and tags not mapped";
        if (hasAccount)
            return "Account not mapped";
        return "Tags not matched";
    }

    // ─── Creditor data (Feature 2) ──────────────────────────────────────────────

    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 40000099;

    // Holded caps a dailyledger window at one year; 364 days stays safely under.
    private static readonly Duration LedgerWindow = Duration.FromDays(364);
    // Backstop on the first-run backward sweep (~25 years); logged if hit so a cap is never silent.
    private const int BackfillWindowCap = 25;

    public async Task SyncCreditorLedgerAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        try
        {
            var latest = await repo.GetLatestLedgerLineDateAsync(ct);

            // First run (empty cache) → full-history backfill in ≤1-year backward windows.
            // Steady state → incremental append from the latest cached line forward, also in ≤1-year
            // windows so a long gap (sync disabled or no creditor activity for >1 year) still catches up
            // instead of failing on an over-wide request. Idempotent upsert on (EntryNumber, Line) makes
            // re-fetching the boundary safe.
            var fetched = latest is null
                ? await BackfillLedgerAsync(now, ct)
                : await IncrementalLedgerAsync(latest.Value, now, ct);

            // Persist only creditor-account (400000xx) lines — the only accounts the read paths derive
            // from. The dailyledger has no server-side account filter, so the fetch sweeps the whole
            // daybook regardless; we keep the subset we use. (Budget actuals run on a separate path.)
            var lines = fetched
                .Where(l => l.AccountNum >= CreditorAccountMin && l.AccountNum <= CreditorAccountMax)
                .Select(l => new HoldedLedgerLine
                {
                    Id = Guid.NewGuid(),
                    EntryNumber = l.EntryNumber,
                    Line = l.Line,
                    AccountNum = l.AccountNum,
                    Date = l.Date,
                    Type = l.Type,
                    Description = l.Description,
                    Debit = l.Debit,
                    Credit = l.Credit,
                    CreatedAt = now,
                    LastSyncedAt = now,
                })
                .ToList();

            await repo.UpsertLedgerLinesAsync(lines, now, ct);

            logger.LogInformation(
                "Holded ledger sync ({Mode}) cached {Count} creditor journal lines",
                latest is null ? "backfill" : "incremental", lines.Count);
        }
        catch (Exception ex)
        {
            // Surface the failure in the same sync-state widget the actuals pull uses, then rethrow
            // so Hangfire records the job failure too.
            logger.LogError(ex, "HoldedFinanceService.SyncCreditorLedgerAsync failed");
            try
            {
                var state = await repo.GetSyncStateAsync(CancellationToken.None);
                state.SyncStatus = HoldedSyncStatus.Error;
                state.LastError = ex.Message;
                state.StatusChangedAt = now;
                await repo.SaveSyncStateAsync(state, CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Failed to persist creditor-sync error state");
            }
            throw;
        }
    }

    /// <summary>Sweeps inception→now in ≤1-year backward windows, stopping at the first empty window
    /// (the org's books are contiguous back to inception). Logs if the window cap is hit (no silent caps).</summary>
    private async Task<IReadOnlyList<HoldedLedgerLineDto>> BackfillLedgerAsync(Instant now, CancellationToken ct)
    {
        var all = new List<HoldedLedgerLineDto>();
        var to = now;
        for (var window = 0; window < BackfillWindowCap; window++)
        {
            var from = to.Minus(LedgerWindow);
            var page = await client.ListDailyLedgerAsync(from, to, ct);
            if (page.Count == 0)
                return all;
            all.AddRange(page);
            to = from.Minus(Duration.FromSeconds(1));
        }

        logger.LogWarning(
            "Holded ledger backfill hit the {Cap}-window cap; journal history older than {To} was not swept.",
            BackfillWindowCap, to);
        return all;
    }

    /// <summary>Sweeps forward from the latest cached line in ≤1-year windows (the API rejects wider
    /// ranges). In steady state this is a single window; after a long dormancy it chunks so the sync
    /// catches up rather than failing on an over-wide request.</summary>
    private async Task<IReadOnlyList<HoldedLedgerLineDto>> IncrementalLedgerAsync(Instant from, Instant now, CancellationToken ct)
    {
        var all = new List<HoldedLedgerLineDto>();
        var windowStart = from;
        while (windowStart < now)
        {
            var windowEnd = windowStart.Plus(LedgerWindow);
            if (windowEnd > now) windowEnd = now;
            all.AddRange(await client.ListDailyLedgerAsync(windowStart, windowEnd, ct));
            windowStart = windowEnd.Plus(Duration.FromSeconds(1));
        }
        return all;
    }

    public async Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default)
    {
        if (supplierAccountNum is not { } num)
            return null;

        var lines = await repo.GetLedgerLinesByAccountNumAsync(num, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        var payments = LedgerPayments(lines);

        return new HoldedCreditorStatus(
            SupplierAccountNum: num,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            LastPaymentDate: payments.Count == 0 ? null : payments.Max(p => p.Date),
            TotalPaid: payments.Sum(p => p.Amount),
            Payments: payments);
    }

    // ── Ledger derivations (sign confirmed against live data: Daniela 40000001
    //    credit 12720 − debit 9540 = 3180 owed; chart showed −3180) ──────────────
    //    balance = Σdebit − Σcredit (negative = org owes); owed = max(0, −balance); payments = debit lines.

    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLine> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    private static List<HoldedPaymentInfo> LedgerPayments(IEnumerable<HoldedLedgerLine> lines)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return lines
            .Where(l => l.Debit > 0m)
            .Select(l => new HoldedPaymentInfo(l.Date.InZone(zone).Date, l.Debit, l.Type))
            .ToList();
    }

    // ─── Creditor bindings + statement ──────────────────────────────────────────

    public async Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts,
                       IReadOnlyList<CreditorContactBinding> Unresolved)> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        var byAccount = (await repo.GetAllLedgerLinesAsync(ct))
            .GroupBy(l => l.AccountNum)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<HoldedLedgerLine>)g.ToList());

        // Holded is the only place the chart-account label lives — nothing caches it locally.
        // Range filter is load-bearing: Holded assigns a supplier number to every supplier contact,
        // so an ordinary org vendor would otherwise become a bindable "creditor account" here.
        // Group-by-first: a duplicate account number must not throw the whole list.
        var contacts = (await ListContactsOrEmptyAsync(ct))
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax)
            .GroupBy(c => c.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Which 400000xx a contact carries is Holded's fact, so this map — not the number cached on the
        // binding — decides the row, and it does two jobs. A binding whose one-shot number resolution
        // missed carries a contact id and a null 400000xx; keyed on the number alone its account renders
        // "unbound" while a member in fact holds the contact behind it, which is both a lie and the
        // reason such a binding could not be unbound from here. And the two columns are independent, so
        // bindings sharing a contact can carry numbers that disagree — resolving through the contact
        // lands them on one row, which is what makes the contact-id half of the at-most-one-member
        // invariant (the half FindConflictingBinding enforces on writes) visible as a collision instead
        // of two innocent-looking single-member rows. The stored number is the fallback, for a contact
        // Holded's list does not carry.
        var accountByContactId = contacts
            .GroupBy(kv => kv.Value.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        // Resolve every binding to its account once, then split on the outcome. The two halves are one
        // partition of one snapshot rather than two independently-derived filters: an account row and
        // the unresolved card must never both claim the same binding, and deriving them separately
        // would let a mid-read change to either input produce exactly that.
        var resolved = (await repo.GetCreditorContactsAsync(ct))
            .Select(b => (Account: accountByContactId.TryGetValue(b.HoldedContactId, out var viaContact)
                              ? viaContact
                              : b.SupplierAccountNum,
                          Binding: b))
            .ToList();

        // Every binding on an account, not just the first: only UserId is unique in the DB and the two
        // automatic write paths record what Holded assigned rather than refusing, so a second member on
        // one 400000xx is exactly the state an admin has to see and resolve here.
        var bindings = resolved
            .Where(x => x.Account is not null)
            .GroupBy(x => x.Account!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CreditorContactBinding>)g
                    .OrderBy(x => x.Binding.CreatedAt)
                    .Select(x => ToBinding(x.Binding))
                    .ToList());

        // The remainder: no number of our own and none on Holded's contact either, so no row below can
        // carry it. Nothing retries these (nobodies-collective/Humans#972), so they are returned
        // alongside the rows rather than dropped — unreturned is unbindable.
        var unresolved = resolved
            .Where(x => x.Account is null)
            .OrderBy(x => x.Binding.CreatedAt)
            .Select(x => ToBinding(x.Binding))
            .ToList();

        // Every creditor account with ledger activity, plus bound accounts that have no lines yet,
        // plus every Holded creditor contact — a first-time submitter's account exists in Holded
        // before it has any journal activity, and that is exactly the row an admin needs to see.
        var rows = byAccount.Keys.Union(bindings.Keys).Union(contacts.Keys)
            .Select(num =>
            {
                decimal? balance = byAccount.TryGetValue(num, out var lines) ? LedgerBalance(lines) : null;
                bindings.TryGetValue(num, out var bound);
                contacts.TryGetValue(num, out var contact);
                return new HoldedCreditorAccountRow(
                    SupplierAccountNum: num,
                    Name: contact?.Name ?? "",
                    Balance: balance,
                    OwedToMember: balance is { } b ? Math.Max(0m, -b) : 0m,
                    Bindings: bound ?? []);
            }).ToList();

        // Not one account named, yet accounts exist: the contact list came back empty or unusable, and
        // the bind card is down to bare numbers an admin cannot check a member against. That state was
        // silent until a human reported it (nobodies-collective/Humans#994). One row without a name is a
        // real gap in Holded, not a fault here — only the all-or-nothing signature is logged.
        if (rows.Count > 0 && rows.TrueForAll(r => string.IsNullOrWhiteSpace(r.Name)))
            logger.LogWarning(
                "None of the {Count} creditor accounts resolved a Holded contact name; the bind card and " +
                "/Finance/Creditors will show bare account numbers.", rows.Count);

        return (rows, unresolved);
    }

    /// <summary>Holded's contact list, or empty when the Holded call fails. Cached for
    /// <see cref="ContactsCacheDuration"/> (design-rules §15 Option A — short-TTL <see cref="IMemoryCache"/>,
    /// same pattern as the nav-badge counts) since the same identical list is read on every
    /// /Finance/Creditors and /Expenses/{id} load; the TTL keeps a contact created today visible
    /// within minutes without a live call on every page load. The creditor overviews are otherwise
    /// cache-backed reads embedded in admin pages, so a vendor failure must cost the account names,
    /// not the page. Only vendor-call failures are absorbed — anything else is a bug and throws.</summary>
    private async Task<IReadOnlyList<HoldedContactDto>> ListContactsOrEmptyAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKeys.HoldedContacts, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ContactsCacheDuration;
            try
            {
                return await client.ListContactsAsync(ct);
            }
            catch (HoldedTransientException ex)
            {
                logger.LogWarning(ex, "Holded contact list unavailable; creditor account names will be blank.");
                return (IReadOnlyList<HoldedContactDto>)[];
            }
            catch (HoldedPermanentException ex)
            {
                // A rejected key or a removed endpoint blanks every name until someone acts — Error, not Warning.
                logger.LogError(ex, "Holded rejected the contact list; creditor account names will be blank.");
                return (IReadOnlyList<HoldedContactDto>)[];
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Malformed body, or a 200 carrying Holded's {"status":0,...} error object where the
                // contact array should be. Still a vendor failure — it must not take the page down.
                logger.LogError(ex, "Holded returned an unreadable contact list; creditor account names will be blank.");
                return (IReadOnlyList<HoldedContactDto>)[];
            }
        }) ?? [];

    public async Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var b = await repo.GetCreditorContactByUserAsync(userId, ct);
        return b is null ? null : ToBinding(b);
    }

    private static CreditorContactBinding ToBinding(HoldedCreditorContact b) =>
        new(b.UserId, b.HoldedContactId, b.SupplierAccountNum, b.Source);

    // ─── The at-most-one-member invariant (nobodies-collective/Humans#975) ───────
    //
    // A 400000xx account — and the Holded contact behind it — binds to at most one member; a second
    // binding silently points one person's expense payments at another person's creditor account.
    // All three write paths test for it with FindConflictingBinding, and they diverge only in what
    // they do about it, because only one of them is a guess:
    //
    //   SetCreditorContactAsync (manual bind)  — an admin picked the account, so the pick can be
    //       wrong. Refuse and write nothing; the admin re-picks or unbinds the other member.
    //   SetCreditorAccountNumAsync / EnsureCreditorContactAsync (automatic, during an expense push)
    //       — Holded assigned the number to the contact we just pushed, so Holded is authoritative
    //       and the *older* binding is the wrong guess. Refusing would leave a real payable pointing
    //       at nothing to preserve a wrong row. Record the truth, log Error, and let the collision
    //       stand visibly on /Finance/Creditors until an admin resolves it with Unbind.
    //
    // Deliberately not a DB unique index: the automatic writers run unattended inside outbox drain,
    // where a constraint violation would strand a created Holded doc as permanently-failed, and the
    // index would have to be created against production rows that may already collide. Enforcement
    // lives in the service (memory/architecture/db-enforcement-minimal.md).

    /// <summary>Another member's binding already claiming this 400000xx and/or this Holded contact.
    /// Either kind of overlap merges two members' payables, so both are conflicts.</summary>
    private static HoldedCreditorContact? FindConflictingBinding(
        IEnumerable<HoldedCreditorContact> bindings,
        Guid userId,
        int? supplierAccountNum,
        string? holdedContactId) =>
        bindings.FirstOrDefault(b =>
            b.UserId != userId
            && ((supplierAccountNum is not null && b.SupplierAccountNum == supplierAccountNum)
                || (!string.IsNullOrEmpty(holdedContactId)
                    && string.Equals(b.HoldedContactId, holdedContactId, StringComparison.Ordinal))));

    /// <summary>Records a collision the automatic paths wrote through. The admin-facing surface is the
    /// duplicate row on /Finance/Creditors; this is the trail explaining when and how it arrived.</summary>
    private void LogBindingCollision(
        string writePath, Guid userId, string holdedContactId, int? supplierAccountNum,
        HoldedCreditorContact conflict) =>
        logger.LogError(
            "Creditor binding collision in {WritePath}: member {UserId} resolved to Holded contact " +
            "{HoldedContactId} / account {SupplierAccountNum}, which is already bound to member " +
            "{ConflictUserId} ({ConflictSource}). Holded is authoritative so the binding was written; " +
            "both now show on /Finance/Creditors and one must be unbound.",
            writePath, userId, holdedContactId, supplierAccountNum, conflict.UserId, conflict.Source);

    public async Task<CreditorBindResult> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, CancellationToken ct = default)
    {
        // The dropdown is filtered, but it is client data — the account number arrives on a POST.
        // Holded numbers every supplier contact, so without this an org vendor's account is bindable.
        if (supplierAccountNum is < CreditorAccountMin or > CreditorAccountMax)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is outside the member creditor block " +
                $"({CreditorAccountMin}–{CreditorAccountMax}) — that is not a member's account.");

        // Only UserId is unique in the DB, so nothing stops a second member being written onto the
        // same 400000xx. Checked before the Holded call so a doomed bind costs no vendor round-trip.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        if (FindConflictingBinding(bindings, userId, supplierAccountNum, holdedContactId: null) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is already bound to a different member. " +
                "Check /Finance/Creditors to see who, and unbind them there first.");

        var contact = (await client.ListContactsAsync(ct))
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);
        if (contact is null)
            return CreditorBindResult.Failure(
                $"No Holded contact carries account {supplierAccountNum} — nothing bound.");

        // The account-number check above cannot see a member whose binding carries this contact but
        // whose 400000xx never resolved — the push's lookup is best-effort and leaves the number null.
        // Two members on one Holded contact merges their payables just as surely as two on one number,
        // and that binding is invisible on /Finance/Creditors (its rows are keyed by account number),
        // so name the contact rather than send the admin somewhere it does not appear.
        if (FindConflictingBinding(bindings, userId, supplierAccountNum: null, contact.Id) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} belongs to Holded contact \"{contact.Name}\", which is " +
                "already bound to a different member whose account number has not resolved yet. " +
                "Nothing was changed — two members must never share one Holded contact.");

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contact.Id,
            SupplierAccountNum = supplierAccountNum,
            Source = CreditorContactSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct);
        return CreditorBindResult.Success;
    }

    public async Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(
        int supplierAccountNum, CancellationToken ct = default)
    {
        // Reads cached daybook lines only — zero Holded calls per view.
        var lines = await repo.GetLedgerLinesByAccountNumAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        return new HoldedCreditorLedger(
            SupplierAccountNum: supplierAccountNum,
            Name: null,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            Lines: lines.Select(l => new HoldedLedgerLineDto
            {
                EntryNumber = l.EntryNumber,
                Line = l.Line,
                Date = l.Date,
                AccountNum = l.AccountNum,
                Debit = l.Debit,
                Credit = l.Credit,
                Type = l.Type,
                Description = l.Description,
            }).ToList());
    }

    public async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        var allBindings = await repo.GetCreditorContactsAsync(ct);

        // The seed is this member's own cached contact id / 400000xx off a prior report — our guess,
        // not something Holded just told us — so it gets the manual bind's treatment, not
        // SetCreditorAccountNumAsync's: a seed landing on another member's binding is refused rather
        // than recorded. Dropping it makes the push mint this member their own Holded contact, which
        // is the correct split. It is also what makes Unbind durable: the cleared member's old reports
        // still carry the other member's contact id and account number, and adopting those would
        // silently restore the very binding an admin just cleared.
        if (binding is null
            && FindConflictingBinding(allBindings, userId, seedAccountNum, seedContactId) is { } seedConflict)
        {
            logger.LogError(
                "Refused a creditor seed for member {UserId}: the contact {SeedContactId} / account " +
                "{SeedAccountNum} cached on their prior report is bound to member {ConflictUserId} " +
                "({ConflictSource}). Adopting it would merge their payables, so a new Holded contact " +
                "is being created instead.",
                userId, seedContactId, seedAccountNum, seedConflict.UserId, seedConflict.Source);
            seedContactId = null;
            seedAccountNum = null;
        }

        // Reuse the bound contact, else lazy-seed from the report's previously-cached contact id.
        var existingContactId = !string.IsNullOrEmpty(binding?.HoldedContactId)
            ? binding.HoldedContactId
            : (string.IsNullOrEmpty(seedContactId) ? null : seedContactId);

        // Burner goes in tradeName only — and only when it differs from the official legal name.
        var tradeName = !string.IsNullOrWhiteSpace(burnerName)
                        && !string.Equals(burnerName, legalName, StringComparison.Ordinal)
            ? burnerName
            : null;

        var contactId = await client.UpsertContactAsync(new HoldedContactInput
        {
            Name = legalName,
            TradeName = tradeName,
            CustomId = userId.ToString(),
            Type = "creditor",
            Iban = string.IsNullOrWhiteSpace(iban) ? null : iban,
            ExistingContactId = existingContactId,
        }, ct);

        // A refused seed cannot collide, so what is left here is a member whose *own* existing binding
        // already overlaps someone else's — a pre-existing collision this push does not create and
        // must not silently carry forward unreported.
        var accountNum = binding?.SupplierAccountNum ?? seedAccountNum;
        var conflict = FindConflictingBinding(allBindings, userId, accountNum, contactId);
        if (conflict is not null)
            LogBindingCollision(
                nameof(EnsureCreditorContactAsync), userId, contactId, accountNum, conflict);

        // Nothing left to write once the member already held this contact, and writing anyway is worse
        // than wasteful. UpsertContactAsync PUTs to ExistingContactId and returns the id it was given,
        // and Source and the account number are carried straight off the binding just read, so the only
        // column that would change is UpdatedAt — which nothing reads. Meanwhile this write sits on the
        // far side of a multi-second Holded round-trip, holding a copy of the binding read before it:
        // an admin who hits Unbind during that window would get their success message and then have the
        // binding they just cleared resurrected from that stale copy. Skipping the empty write is what
        // makes Unbind hold against an in-flight push in the steady state — a member already bound, with
        // their 400000xx already resolved, does no binding write on a push at all. It does not make the
        // read-modify-write safe in general: a binding still missing its number, and
        // SetCreditorAccountNumAsync below, both write real content and can still lose a concurrent
        // delete (nobodies-collective/Humans#995 — closing that needs an update-only repository write).
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),                                   // ignored on update (keyed by UserId)
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = binding?.Source ?? CreditorContactSource.Auto, // preserve a Manual binding
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct);

        return contactId;
    }

    public async Task SetCreditorAccountNumAsync(
        Guid userId, int supplierAccountNum, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        if (binding is null) return;

        // Holded just told us this number belongs to the contact we pushed against, so it is written
        // either way; only the contact-id overlap is already covered upstream by EnsureCreditorContact.
        var conflict = FindConflictingBinding(
            await repo.GetCreditorContactsAsync(ct), userId, supplierAccountNum, holdedContactId: null);
        if (conflict is not null)
            LogBindingCollision(
                nameof(SetCreditorAccountNumAsync), userId, binding.HoldedContactId,
                supplierAccountNum, conflict);

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = binding.Id,
            UserId = userId,
            HoldedContactId = binding.HoldedContactId,
            SupplierAccountNum = supplierAccountNum,
            Source = binding.Source,
            CreatedAt = binding.CreatedAt,
            UpdatedAt = now,
        }, now, ct);
    }

    public async Task<bool> ClearCreditorContactAsync(Guid userId, CancellationToken ct = default)
    {
        var removed = await repo.DeleteCreditorContactAsync(userId, ct);
        if (removed)
            logger.LogInformation("Cleared the creditor binding for member {UserId}.", userId);
        return removed;
    }

    // ─── GDPR (Article 15 export) ───────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        return
        [
            new UserDataSlice(GdprExportSections.HoldedCreditorAccount,
                binding is null
                    ? null
                    : new
                    {
                        binding.SupplierAccountNum,
                        binding.HoldedContactId,
                        Source = binding.Source.ToString(),
                    }),
        ];
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a dash-free normalized tag for the given group+category.
    /// If the base tag collides with an already-used one, appends the first 4 hex chars of the category id.
    /// </summary>
    private static string UniqueTag(
        string groupName, string categoryName, Guid categoryId,
        HashSet<string> usedTags)
    {
        var baseTag = HoldedMatcher.NormalizeTag(groupName + categoryName);
        if (!usedTags.Contains(baseTag))
            return baseTag;

        // Disambiguate with first 4 hex chars of the category id.
        return baseTag + categoryId.ToString("N")[..4];
    }
}
