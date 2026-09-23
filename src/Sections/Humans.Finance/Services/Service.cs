using Humans.AuditLog.Contracts;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Users.Contracts;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Finance's service: every shape in <c>Docs/health.md</c> §2, over one repository, one clock and
/// one cached Holded contact list.
/// </summary>
internal sealed class Service(
    IHoldedRepository repo,
    IHoldedClient client,
    // Cross-section read via the Budget section's read/write split contract.
    IBudgetServiceRead budget,
    // The ledger mirror moved to the Holded section; all line/balance reads go through its contract.
    IHoldedService holded,
    IClock clock,
    IMemoryCache cache,
    IAuditLogService audit,
    IOptions<SepaOptions> sepa,
    ILogger<Service> logger) : IHoldedFinanceService, IHoldedFinanceAdminService, ISepaBankBooking, IUserDataContributor
{
    internal const string HoldedCreditorAccount = "HoldedCreditorAccount";
    internal const string SepaPayouts = "SepaPayouts";

    private static readonly TimeSpan ContactsCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

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
        // Seeded up front like usedNumbers, not as the walk encounters them: a ToAdd category
        // sorting before a mapped one whose tag it collides with would otherwise be handed that
        // tag verbatim, and two active rows sharing a tag make tag attribution arbitrary.
        var usedTags = map.Where(m => m.IsActive).Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
        var currentActiveCatIds = categories.Select(c => c.Id).ToHashSet();

        int nextFree = blockStart;

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
            }
            else
            {
                var tag = UniqueTag(groupName, catName, catId, usedTags);
                usedTags.Add(tag);

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

        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        state.Status = "Running";
        state.StatusChangedAt = now;
        await repo.SaveDocSyncStateAsync(state, ct);

        try
        {
            var map = await repo.GetCategoryMapAsync(ct);
            var entries = map
                .Where(m => m.IsActive)
                .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.Tag))
                .ToArray();

            var allDocs = await client.ListPurchaseDocumentsAsync(ct);

            var docs = allDocs.Select(doc => MapDoc(doc, entries, now)).ToList();

            await repo.UpsertDocsAsync(docs, now, ct);

            var matched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
            var unmatched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Unmatched);

            state.Status = "Idle";
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastError = null;
            state.LastSyncedDocCount = docs.Count;
            await repo.SaveDocSyncStateAsync(state, ct);

            return new HoldedSyncResult(docs.Count, matched, unmatched);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Service.SyncAsync failed");
            state.Status = "Error";
            state.LastError = ex.Message;
            state.StatusChangedAt = now;
            try { await repo.SaveDocSyncStateAsync(state, CancellationToken.None); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist error sync state"); }
            throw;
        }
    }

    public async Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default)
    {
        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        // The binding count rides along from the repo so /Holded never has to build the full
        // creditor-account view (a live Holded contacts walk) just to show a number.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        return new HoldedDocSyncInfo(
            state.LastSyncAt, state.Status, state.LastError, state.LastSyncedDocCount, bindings.Count);
    }

    private static HoldedExpenseDoc MapDoc(
        HoldedPurchaseDocListItemDto doc,
        HoldedMatchEntry[] entries,
        Instant now)
    {
        // The whole doc goes on its FIRST line's account, with the union of doc and line tags:
        // real purchase docs are single-line today. Line-level attribution is a later refinement.
        var bookedAccount = doc.Lines.Count > 0 ? doc.Lines[0].AccountId : null;
        var tags = doc.Tags
            .Concat(doc.Lines.SelectMany(l => l.Tags))
            .ToList();

        var matchResult = HoldedMatcher.Match(bookedAccount, tags, entries);

        var localDate = doc.Date.InZone(MadridZone).Date;

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
            // Strict equality, not `!= true`: an absent `draft` field is treated as NOT approved,
            // the same caution the old draft-id sweep applied to a doc it couldn't place — a
            // silently-approved doc leaks into the budget actuals, an unmatched-approval one just
            // stays a gap on the Unmatched queue.
            IsApproved = doc.IsDraft == false,
            TagsJson = JsonSerializer.Serialize(tags),
            BookedAccountId = bookedAccount,
            BudgetCategoryId = matchResult.CategoryId,
            MatchStatus = matchResult.CategoryId is null
                ? HoldedMatchStatus.Unmatched
                : HoldedMatchStatus.Matched,
            MatchSource = matchResult.Source,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ─── Actuals ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default)
    {
        // Doc-derived, not ledger balances: the budget pages are gross/IVA-inclusive while a 629
        // balance is net, and the ledger carries drafts Holded has not approved.
        var docs = await repo.GetMatchedForYearAsync(calendarYear, ct);
        return docs
            .Where(d => d.IsApproved == true && d.BudgetCategoryId is not null)
            .GroupBy(d => d.BudgetCategoryId!.Value)
            .Select(g => new HoldedActualRow(
                g.Key,
                g.Sum(d => d.Total),
                g.OrderByDescending(d => d.Date)
                    .ThenBy(d => d.DocNumber, StringComparer.Ordinal)
                    .Select(d => new HoldedActualDoc(
                        d.HoldedDocId, d.DocNumber, d.ContactName, d.Date, d.Total, HoldedDocUrl(d.HoldedDocId)))
                    .ToList()))
            .Where(r => r.Actual != 0m)
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
                HoldedDocUrl(d.HoldedDocId)))
            .ToList();
    }

    private static string HoldedDocUrl(string holdedDocId) =>
        $"https://app.holded.com/purchases/{holdedDocId}";

    public async Task<string?> GetHoldedAccountIdForCategoryAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        return map.FirstOrDefault(m => m.IsActive && m.BudgetCategoryId == budgetCategoryId)?.HoldedAccountId;
    }

    // ─── Connector overview (/Finance/Holded) ─────────────────────────────────────

    public async Task<HoldedConnectorVm> GetConnectorOverviewAsync(CancellationToken ct = default)
    {
        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var map = await repo.GetCategoryMapAsync(ct);
        var docs = await repo.GetAllDocsAsync(ct);

        // Category names come from the active budget year, the same source the provisioning plan
        // uses. A map row or doc pointing outside it keeps a null name rather than a lookup per row.
        var year = await budget.GetActiveYearAsync();
        var categories = year is null
            ? new Dictionary<Guid, (string Name, string Group)>()
            : year.Groups
                .SelectMany(g => g.Categories.Select(c => (c.Id, Name: c.Name, Group: g.Name)))
                .ToDictionary(c => c.Id, c => (c.Name, c.Group));

        string? NameOf(Guid? categoryId) =>
            categoryId is { } id && categories.TryGetValue(id, out var c) ? c.Name : null;

        var now = clock.GetCurrentInstant();
        var age = state.LastSyncAt is { } last ? now - last : (Duration?)null;

        return new HoldedConnectorVm(
            new HoldedDocSyncVm(
                state.LastSyncAt,
                state.Status,
                state.LastError,
                state.LastSyncedDocCount,
                age,
                // Never having run is stale too: the actuals and the unmatched queue are empty for
                // the same reason a stalled sync leaves them wrong, and both need the same alarm.
                IsStale: age is null || age >= HoldedDocSyncVm.StaleAfter,
                // A failed run moves only this: SyncAsync leaves LastSyncAt on the older success.
                // Dropping it left an Error row unable to say when the failure actually happened.
                state.StatusChangedAt),
            bindings.Count,
            map.Select(m => new HoldedCategoryMapVm(
                m.BudgetCategoryId,
                NameOf(m.BudgetCategoryId),
                categories.TryGetValue(m.BudgetCategoryId, out var g) ? g.Group : null,
                m.HoldedAccountNumber,
                m.HoldedAccountId,
                m.Tag,
                m.IsActive,
                m.UpdatedAt)).ToList(),
            docs.Select(d => new HoldedDocVm(
                d.HoldedDocId,
                d.DocNumber,
                d.ContactName,
                d.Date,
                d.Total,
                d.IsApproved,
                d.MatchStatus,
                d.MatchSource,
                NameOf(d.BudgetCategoryId),
                d.BookedAccountId,
                d.TagsJson,
                d.LastSyncedAt)).ToList());
    }

    private static string ReasonFor(HoldedExpenseDoc d)
    {
        var hasAccount = !string.IsNullOrEmpty(d.BookedAccountId);
        // MapDoc always serializes a list, so "[]" is the only empty spelling.
        var hasTags = !string.Equals(d.TagsJson, "[]", StringComparison.Ordinal);

        if (!hasAccount && !hasTags)
            return "No account, no tag";
        if (hasAccount && hasTags)
            return "Account and tags not mapped";
        if (hasAccount)
            return "Account not mapped";
        return "Tags not matched";
    }

    // ─── Creditor data (Feature 2) ──────────────────────────────────────────────

    // A new ER-only contact still gets type "creditor" (Peter, 2026-08-25), which Holded mints
    // in the 410-series rather than the 400-series proveedor accounts older members carry — so
    // the block spans both. Bindings, not the range, are what separate a member's account from
    // an ordinary org vendor's (the one-member-per-account conflict checks guard that).
    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 41999999;

    public async Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default)
    {
        if (supplierAccountNum is not { } num)
            return null;

        var lines = await holded.GetLedgerLinesAsync(num, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        var payments = lines.Where(l => l.Debit > 0m).ToList();

        return new HoldedCreditorStatus(
            SupplierAccountNum: num,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            LastPaymentDate: payments.Count == 0
                ? null
                : payments.Max(l => l.Date).InZone(MadridZone).Date,
            TotalPaid: payments.Sum(l => l.Debit));
    }

    // Sign confirmed against live data (Daniela 40000001: credit 12720 − debit 9540 = 3180 owed,
    // chart showed −3180). Payments out are the debit lines.
    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLineInfo> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    // ─── Creditor bindings + statement ──────────────────────────────────────────

    public async Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts,
                       IReadOnlyList<CreditorContactBinding> Unresolved)> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        var byAccount = await holded.GetAccountBalancesAsync(ct: ct);

        // Who a binding names is the binding's own fact, so this map is not range-filtered — the
        // range decides which accounts are creditor accounts, not which contact is the member's.
        var contactById = await ContactsByIdAsync(ct);

        // Holded is the only place the account label lives. The range filter is load-bearing: Holded
        // numbers every supplier contact, so unfiltered, an org vendor becomes a bindable creditor
        // account. Group-by-first so a duplicate number cannot throw the whole list.
        var contacts = contactById.Values
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax)
            .GroupBy(c => c.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Which 400000xx a contact holds is Holded's fact, so it — not the number cached on the binding
        // — decides the row: a binding whose number never resolved still reaches its account, and two
        // bindings on one contact land together as the collision they are. Stored number is the fallback.
        var accountByContactId = contacts
            .GroupBy(kv => kv.Value.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        // Resolved once and split, not filtered twice: one partition of one snapshot, so no binding can
        // appear both on an account row and on the unresolved card.
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

        // Accounts with ledger activity, plus bound ones with no lines yet, plus every Holded creditor
        // contact — a first-time submitter's account exists before it has any journal activity. The
        // mirror spans the whole chart, so its balances are range-filtered here.
        var rows = byAccount.Keys
            .Where(num => num is >= CreditorAccountMin and <= CreditorAccountMax)
            .Union(bindings.Keys).Union(contacts.Keys)
            .Select(num =>
            {
                decimal? balance = byAccount.TryGetValue(num, out var b) ? b : null;
                bindings.TryGetValue(num, out var bound);

                // Holded can carry two contacts on one 400000xx, and then the account's first
                // contact is not necessarily the bound member's. A singly-bound row therefore
                // takes its name and IBAN from the contact the binding names — that contact is
                // what a payout would pay, so it is what the row has to show. Unbound and
                // colliding rows are unpayable anyway and keep the account's contact as a label.
                var contact = bound is { Count: 1 }
                    ? contactById.GetValueOrDefault(bound[0].HoldedContactId)
                    : contacts.GetValueOrDefault(num);

                return new HoldedCreditorAccountRow(
                    SupplierAccountNum: num,
                    Name: contact?.Name ?? "",
                    Balance: balance,
                    OwedToMember: balance is { } bal ? Math.Max(0m, -bal) : 0m,
                    Bindings: bound ?? [],
                    IbanMasked: string.IsNullOrWhiteSpace(contact?.Iban)
                        ? null
                        : IbanFormatter.Mask(contact.Iban));
            }).ToList();

        // Accounts but not one name is the signature of an unusable contact list, which leaves the bind
        // card as bare numbers. Silent until a human reported it (nobodies-collective/Humans#994). One
        // missing name is a real gap in Holded, so only the all-or-nothing case is logged.
        if (rows.Count > 0 && rows.TrueForAll(r => string.IsNullOrWhiteSpace(r.Name)))
            logger.LogWarning(
                "None of the {Count} creditor accounts resolved a Holded contact name; the bind card and " +
                "/Finance/Creditors will show bare account numbers.", rows.Count);

        return (rows, unresolved);
    }

    /// <summary>Holded's contact list keyed by contact id — the identity a creditor binding stores.
    /// Both the account rows and the payout resolve their contact through this one map, so the name
    /// and IBAN a row displays cannot disagree with the ones the file is built from.</summary>
    private async Task<Dictionary<string, HoldedContactDto>> ContactsByIdAsync(CancellationToken ct) =>
        (await ListContactsOrEmptyAsync(ct))
        .GroupBy(c => c.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Holded's contact list, cached for <see cref="ContactsCacheDuration"/> (design-rules §15
    /// Option A) because every /Finance/Creditors and /Expenses/{id} load reads the same list. A vendor
    /// failure costs the account names, not the page; anything else is a bug and throws.</summary>
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
    // An account, and the Holded contact behind it, binds to at most one member. All three write paths
    // check with FindConflictingBinding and differ only in the remedy, by whether the value is our guess
    // (refuse) or Holded's own statement (write it and let the collision show); each says so at its own
    // call site. Not a unique DB index — see Docs/health.md, "deliberately not done".

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

    /// <summary>True when the member's creditor row is still exactly what <paramref name="asRead"/> holds
    /// — same row, same content, or still absent. Compared field by field, not by row identity: the upsert
    /// is keyed by UserId and mutates in place, so an admin's rebind keeps the Id and changes only the
    /// columns a stale write would clobber (nobodies-collective/Humans#995).</summary>
    private async Task<bool> BindingUnchangedAsync(
        string writePath, Guid userId, HoldedCreditorContact? asRead, CancellationToken ct)
    {
        var current = await repo.GetCreditorContactByUserAsync(userId, ct);
        if (current is null && asRead is null) return true;
        if (current is not null && asRead is not null
            && current.Id == asRead.Id
            && string.Equals(current.HoldedContactId, asRead.HoldedContactId, StringComparison.Ordinal)
            && current.SupplierAccountNum == asRead.SupplierAccountNum
            && current.Source == asRead.Source)
            return true;

        // The admin's action stands and nothing is written, so this line is the only record that a push
        // was overtaken — worth a Warning so a member whose push "did nothing" can be explained.
        logger.LogWarning(
            "Skipped the creditor binding write in {WritePath} for member {UserId}: the binding changed " +
            "while the push was in flight — was {WasContactId} / {WasAccountNum} ({WasSource}), is now " +
            "{NowContactId} / {NowAccountNum} ({NowSource}). The newer binding stands.",
            writePath, userId,
            asRead?.HoldedContactId, asRead?.SupplierAccountNum, asRead?.Source,
            current?.HoldedContactId, current?.SupplierAccountNum, current?.Source);
        return false;
    }

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
        // Lines come from the mirror and the header off the cached contact list: no Holded call.
        var lines = await holded.GetLedgerLinesAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        var contact = (await ListContactsOrEmptyAsync(ct))
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);

        var balance = LedgerBalance(lines);
        return new HoldedCreditorLedger(
            SupplierAccountNum: supplierAccountNum,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            Lines: lines.Select(l => new CreditorLedgerLine
            {
                EntryNumber = l.EntryNumber,
                Line = l.Line,
                Date = l.Date,
                AccountNum = l.AccountNum,
                Debit = l.Debit,
                Credit = l.Credit,
                Type = l.Type,
                Description = l.Description,
            }).ToList(),
            Contact: contact is null
                ? null
                : new HoldedContactInfo(
                    contact.Name, contact.TradeName, contact.Email, contact.Phone,
                    contact.Mobile, contact.Iban, contact.TaxCode, contact.Address));
    }

    public async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        var allBindings = await repo.GetCreditorContactsAsync(ct);

        // The seed is our own guess off a prior report, not Holded's word, so it gets the manual
        // bind's remedy: refuse, and let the push mint this member their own contact. That is also
        // what makes Unbind durable — an old report still carries the contact just unbound.
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

        // A linked contact is used as is: the bound contact, else the one lazy-seeded from the
        // member's prior report. Never a PUT — Holded's v2 contact update is a full replacement, so
        // every field the body omits resets, supplier_record included, and the next purchase doc
        // then mints the member a second creditor account next to their first (2026-09-21). A
        // legal-name or IBAN change after the first push does not reach Holded until the
        // link-check sync exists (peterdrier/Humans#1777).
        string contactId;
        if (!string.IsNullOrEmpty(binding?.HoldedContactId))
            contactId = binding.HoldedContactId;
        else if (!string.IsNullOrEmpty(seedContactId))
            contactId = seedContactId;
        else
        {
            // Burner goes in tradeName only — and only when it differs from the official legal name.
            var tradeName = !string.IsNullOrWhiteSpace(burnerName)
                            && !string.Equals(burnerName, legalName, StringComparison.Ordinal)
                ? burnerName
                : null;

            contactId = await client.UpsertContactAsync(new HoldedContactInput
            {
                Name = legalName,
                TradeName = tradeName,
                CustomId = userId.ToString(),
                Type = "creditor",
                Iban = string.IsNullOrWhiteSpace(iban) ? null : iban,
            }, ct);
        }

        // A refused seed cannot collide, so anything left is a pre-existing overlap on the member's own
        // binding — not this push's doing, but not to be carried forward unreported either.
        var accountNum = binding?.SupplierAccountNum ?? seedAccountNum;
        var conflict = FindConflictingBinding(allBindings, userId, accountNum, contactId);
        if (conflict is not null)
            LogBindingCollision(
                nameof(EnsureCreditorContactAsync), userId, contactId, accountNum, conflict);

        // A member who already holds this contact has nothing to write but UpdatedAt, which nothing
        // reads — and the rest of the push is several Holded calls long, so a binding row written
        // from this copy would land over whatever an admin did meanwhile. Skipping it is what makes
        // Unbind hold against an in-flight push.
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        // A binding still missing its number writes real content, so it cannot be skipped — but it can
        // still undo an admin's Bind or Unbind from the copy read before the create round-trip.
        // Re-reading here shrinks that window without a version column (nobodies-collective/Humans#995).
        if (!await BindingUnchangedAsync(nameof(EnsureCreditorContactAsync), userId, binding, ct))
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

        // Holded's own word, so it is written either way; the contact-id overlap is covered upstream.
        var conflict = FindConflictingBinding(
            await repo.GetCreditorContactsAsync(ct), userId, supplierAccountNum, holdedContactId: null);
        if (conflict is not null)
            LogBindingCollision(
                nameof(SetCreditorAccountNumAsync), userId, binding.HoldedContactId,
                supplierAccountNum, conflict);

        // Same re-check as EnsureCreditorContactAsync: the row below carries the contact id, Source and
        // CreatedAt read above, so writing it over an admin's newer binding reverts it wholesale
        // (nobodies-collective/Humans#995).
        if (!await BindingUnchangedAsync(nameof(SetCreditorAccountNumAsync), userId, binding, ct)) return;

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

    // ─── SEPA payout (nobodies-collective/Humans#1134) ──────────────────────────

    // Persisted in audit_log.entity_type; pinned so a rename cannot silently orphan old rows.
    private const string SepaTransferEntityType = nameof(SepaPayoutTransfer);

    public SepaPayoutSettings GetSepaPayoutSettings()
    {
        // Never inferred: a file presented under a guessed name, account or presenter id is rejected
        // by the bank at best, and paid out of the wrong account at worst.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(sepa.Value.CreditorName)) missing.Add("Sepa:CreditorName");
        if (string.IsNullOrWhiteSpace(sepa.Value.CreditorIban)) missing.Add("Sepa:CreditorIban");
        if (string.IsNullOrWhiteSpace(sepa.Value.CreditorIdentifier)) missing.Add("Sepa:CreditorIdentifier");

        return new SepaPayoutSettings(
            sepa.Value.MaxPayoutPerTransfer,
            missing.Count == 0
                ? null
                : $"SEPA payout is unavailable — not configured: {string.Join(", ", missing)}.");
    }

    public async Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default)
    {
        var settings = GetSepaPayoutSettings();
        if (settings.UnavailableReason is { } unavailable)
            return SepaPayoutResult.Failure(unavailable);

        if (selections.Count == 0)
            return SepaPayoutResult.Failure("No creditor account was selected — nothing to generate.");

        if (selections.GroupBy(s => s.SupplierAccountNum).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            return SepaPayoutResult.Failure(
                $"Creditor account {duplicate.Key} was selected twice — a payout pays each account once.");

        var (rows, _) = await ListCreditorAccountsAsync(ct);
        var byAccount = rows.ToDictionary(r => r.SupplierAccountNum);

        // The unmasked IBAN is read here and goes nowhere but the file and the payout record. Keyed
        // by contact id, never by account number: two Holded contacts can share one 400000xx, and
        // only the one the binding names is the member's — by-account would pay the other one.
        var contactById = await ContactsByIdAsync(ct);

        var fileId = Guid.NewGuid();
        var transfers = new List<SepaPayoutTransfer>(selections.Count);

        foreach (var s in selections)
        {
            if (!byAccount.TryGetValue(s.SupplierAccountNum, out var row))
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum} is no longer listed — reload the page and try again.");

            if (row.Bindings.Count != 1)
                return SepaPayoutResult.Failure(row.Bindings.Count == 0
                    ? $"Creditor account {s.SupplierAccountNum} is not bound to a member — bind it first."
                    : $"Creditor account {s.SupplierAccountNum} is bound to {row.Bindings.Count} members — "
                      + "unbind all but the one who owns it first.");

            if (s.Amount > row.OwedToMember)
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum}: {Euros(s.Amount)} is more than the "
                    + $"{Euros(row.OwedToMember)} owed.");

            // Name and IBAN both come off the bound contact, so a transfer cannot carry one
            // person's name over another person's account.
            if (!contactById.TryGetValue(row.Bindings[0].HoldedContactId, out var contact)
                || string.IsNullOrWhiteSpace(contact.Iban))
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum} has no IBAN on the Holded contact it is bound to.");

            transfers.Add(new SepaPayoutTransfer
            {
                Id = Guid.NewGuid(),
                FileId = fileId,
                UserId = row.Bindings[0].UserId,
                SupplierAccountNum = s.SupplierAccountNum,
                HoldedContactId = row.Bindings[0].HoldedContactId,
                CreditorName = SepaText.Normalize(contact.Name, SepaPaymentFileBuilder.MaxNameLength),
                Iban = IbanValidator.Normalize(contact.Iban),
                IbanMasked = IbanFormatter.Mask(contact.Iban),
                Amount = s.Amount,
            });
        }

        var now = clock.GetCurrentInstant();
        // Ids are minted before the file is built and never change: the persisted row is what the
        // bank's EndToEndId points back at.
        var request = new SepaPaymentFileRequest(
            MsgId: "M" + fileId.ToString("N"),
            PmtInfId: "P" + fileId.ToString("N"),
            CreatedAt: now,
            RequestedExecutionDate: now.InZone(MadridZone).Date,
            Debtor: new SepaDebtor(
                sepa.Value.CreditorName!, sepa.Value.CreditorIban!,
                sepa.Value.CreditorBic, sepa.Value.CreditorIdentifier!),
            MaxAmountPerTransfer: maxPerTransfer,
            Transfers: transfers
                .Select(t => new SepaTransfer(
                    "E" + t.Id.ToString("N"), t.CreditorName, t.Iban, t.Amount, t.SupplierAccountNum))
                .ToList());

        string xml;
        try
        {
            xml = SepaPaymentFileBuilder.Build(request);
        }
        catch (SepaPaymentFileException ex)
        {
            // The builder masks any IBAN it names, so the message is safe to log and to show.
            logger.LogWarning("SEPA payout generation refused: {Reason}", ex.Message);
            return SepaPayoutResult.Failure(ex.Message);
        }

        // The stamp is minute-resolution, so it alone would give two batches in one minute the same
        // name — and the name is what reconciles a file on the treasurer's disk against its row, and
        // what the audit line quotes. The file id's first 8 hex characters settle it.
        var fileName = $"{FileSlug(sepa.Value.CreditorName!)}-"
                     + $"{now.InZone(MadridZone).ToDateTimeUnspecified().ToFileTimestamp()}-"
                     + $"{fileId.ToString("N")[..8]}.xml";

        await repo.AddSepaPayoutAsync(new SepaPayoutFile
        {
            Id = fileId,
            GeneratedAt = now,
            GeneratedByUserId = actorUserId,
            FileName = fileName,
            Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant(),
            Xml = xml,
        }, transfers, ct);

        // After the save, per IAuditLogService's contract — a rolled-back write must not leave a ghost row.
        foreach (var t in transfers)
            await audit.LogAsync(
                AuditAction.SepaPayoutTransfer, SepaTransferEntityType, t.Id,
                $"SEPA payout {Euros(t.Amount)} to {t.IbanMasked} on creditor account "
                + $"{t.SupplierAccountNum} (file {fileName}).",
                actorUserId, t.UserId, nameof(User));

        return new SepaPayoutResult(fileName, xml, null);
    }

    // ─── SEPA booking against the bank line (nobodies-collective/Humans#1185) ───

    /// <summary>The job name the sweep's audit entries carry in place of a human actor.</summary>
    private const string SepaBookingJobName = "sepa-bank-booking";

    /// <summary>Below half a cent is zero at two decimal places.</summary>
    private const decimal CentEpsilon = 0.005m;

    /// <summary>Serialises every SEPA booking on this server. The row's <c>BookedAt</c> is read
    /// before the Holded postings and written after them, so without this two concurrent bookings of
    /// one transfer both read "not booked" and both post the whole amount.</summary>
    private static readonly SemaphoreSlim BookingGate = new(1, 1);

    /// <summary>The slack on either end of the tagged-lines window. The window itself spans the
    /// file's generation day to today — a posting this flow made is dated the bank line, but one a
    /// pre-#1185 run made is dated the click.</summary>
    private const int TagWindowDays = 7;

    /// <summary>How far back a transfer's file may have been generated and still be matched. The feed
    /// itself is read <see cref="GenerationSlackDays"/> further back (<see cref="FeedFrom"/>), because a
    /// line may pay a file dated up to that many days after it.</summary>
    private const int FeedWindowDays = 90;

    private const string ReconciledStatus = "reconciled";

    private const string PendingStatus = "pending";

    /// <summary>How far before its file's generation day a bank line may be dated and still be
    /// paying it — the bank can value-date a day early, and the feed's dates are Madrid days.</summary>
    private const int GenerationSlackDays = 2;

    /// <summary>The remittance text the payout file writes — <c>&lt;account&gt; - NCA - &lt;name&gt;</c>.
    /// Matched anywhere in the line, not anchored: bank feeds prepend their own wording to the
    /// Ustrd.</summary>
    private static readonly Regex RemittanceAccount = new(
        @"(?<acct>\d{8})\s*-\s*NCA\s*-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public async Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError)>
        GetSepaPayoutsAsync(CancellationToken ct = default)
    {
        var rows = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var unavailable = BookingUnavailableReason();
        if (rows.Count == 0 || unavailable is not null) return (rows, unavailable, [], null);

        // UserId is the one column the DB keeps unique, so this cannot throw.
        var bindingByUser = (await repo.GetCreditorContactsAsync(ct)).ToDictionary(c => c.UserId);
        var withReasons = rows.Select(r => r with { NotBookableReason = NotBookableReason(r) }).ToList();

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await ReadBankFeedAsync(FeedFrom(), ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            // The page still lists everything it lists; it just cannot offer a line to book against.
            logger.LogWarning(ex, "The Sabadell bank feed could not be read for /Finance/Sepa.");
            return (withReasons, null, [],
                "The Sabadell bank feed could not be read, so no transfer can be booked right now.");
        }

        var bookable = withReasons.Where(r => r.NotBookableReason is null).ToList();
        var unmatched = new List<SepaBankMovementVm>();
        var candidateByTransfer = new Dictionary<Guid, HoldedBankMovementDto>();
        var considered = movements
            .Where(NeedsAMatch)
            // A line this section already booked is nobody's problem — it renders on its own row.
            .Where(m => !withReasons.Any(
                r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            .Select(m =>
            {
                var account = RemittanceAccountNum(m.Description);
                // Only rows that could actually be booked can claim a line; letting a not-bookable
                // row claim one would hide the line from the "needs a human" panel as well as from
                // the row.
                return (Movement: m, Account: account, Matches: account is null
                    ? []
                    : UnbookedMatches(bookable, account.Value, Math.Abs(m.Amount), m.Date));
            })
            .ToList();

        // A transfer two bank lines could each have paid is as ambiguous as a line two transfers
        // could each have asked for: Humans cannot tell which line moved the money, so neither one
        // books it and both go to the panel.
        var claimants = considered
            .Where(c => c.Matches.Count == 1)
            .GroupBy(c => c.Matches[0].TransferId)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (m, account, matches) in considered)
        {
            if (matches.Count == 1 && claimants[matches[0].TransferId] == 1)
            {
                candidateByTransfer[matches[0].TransferId] = m;
                continue;
            }

            unmatched.Add(new SepaBankMovementVm(
                m.Id, m.Date, m.Amount, m.Description, account, m.Status,
                account is null
                    ? "its text names no creditor account"
                    : matches.Count == 0
                        ? $"no unbooked transfer of {Euros(Math.Abs(m.Amount))} on account {account}"
                        : matches.Count > 1
                            ? $"{matches.Count} unbooked transfers match it — settle it in Holded by hand"
                            : "another bank line matches that transfer too — settle it in Holded by hand"));
        }

        return (
            withReasons
                .Select(r => candidateByTransfer.TryGetValue(r.TransferId, out var line)
                    ? r with
                    {
                        CandidateBankMovementId = line.Id,
                        CandidateBankMovementDate = line.Date,
                        CandidateBankMovementAmount = line.Amount,
                        CandidateBankMovementDescription = line.Description,
                    }
                    : r)
                .ToList(),
            null, unmatched, null);

        string? NotBookableReason(SepaPayoutTransferRow row)
        {
            if (row.IsBooked) return null;   // the row renders as booked; no reason to show
            // Nothing in Humans can still book this one: the line that paid it, if any, has dropped
            // off the feed. Say so rather than leaving it in "waiting for the Sabadell line" forever.
            if (IsStale(row))
                return $"generated more than {FeedWindowDays} days ago — no bank line on the feed can "
                       + "book it now; settle it in Holded by hand";
            if (!bindingByUser.TryGetValue(row.UserId, out var binding)
                || string.IsNullOrEmpty(binding.HoldedContactId))
                return "the member has no Holded contact binding";
            if (binding.SupplierAccountNum != row.SupplierAccountNum)
                return "the member's Holded binding changed since this file was generated — book it by hand";
            // Mirrors BookSepaTransferAsync's sibling-contact refusal: same account number is not
            // enough, because Holded lets two contacts share one 400000xx. Without this the button
            // renders live and only fails on click. Null means a row generated before
            // nobodies-collective/Humans#1146 shipped — account-only behaviour, as there.
            if (row.HoldedContactId is { Length: > 0 }
                && !string.Equals(row.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
                return "the member was rebound to a different Holded contact since this file was generated — book it by hand";
            return null;
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (BookingUnavailableReason() is not null || !client.IsConfigured) return;

        var rows = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var pending = rows.Where(r => r.ReconcilePending).ToList();
        var unbooked = rows.Where(r => r.BookedAt is null).ToList();
        if (pending.Count == 0 && unbooked.Count == 0) return;

        // The invariant: every reconcile-pending row's bank line lies inside the window read for it.
        // A pending row's line is already known and already booked — it only has to be looked at
        // again — so its lower bound is its own first payable day with no floor; a row that ages out
        // of the feed window would otherwise never see a human's later reconcile and would keep
        // ReconciledAt and its audit entry missing forever. The feed floor is about matching *new*
        // transfers, so it applies to that bound alone. Widening the read cannot widen matching:
        // UnbookedMatches takes no stale row (GeneratedAt inside the window) and no line dated before
        // the file's first payable day, so every line it can act on is inside the floor anyway. Both bounds key off
        // GeneratedAt, never BookedAt: a line may only pay a file generated on or before it
        // (FirstPayableDate), while a booking can land days later, so a BookedAt window would start
        // *after* the very line a pending row is waiting to see reconciled.
        var floor = FeedFrom();
        var matchFrom = unbooked.Select(FirstPayableDate).DefaultIfEmpty(floor).Min();
        if (matchFrom < floor) matchFrom = floor;
        var pendingFrom = pending.Select(FirstPayableDate).DefaultIfEmpty(matchFrom).Min();
        var from = pendingFrom < matchFrom ? pendingFrom : matchFrom;

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await ReadBankFeedAsync(from, ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "The SEPA sweep could not read the Sabadell bank feed; nothing was booked.");
            return;
        }

        var byId = movements.ToLookup(m => m.Id, StringComparer.Ordinal);

        // Retrying the reconcile call itself would need the posting ids, which this change
        // deliberately does not store. So the retry asks the only question it can: has the line
        // ended up reconciled — by a later sweep's booking, or by a human in the Holded GUI?
        foreach (var row in pending)
        {
            var line = byId[row.HoldedBankMovementId!].FirstOrDefault();
            if (line is null || !IsReconciled(line)) continue;

            var at = clock.GetCurrentInstant();
            await repo.MarkSepaTransferReconciledAsync(row.TransferId, at, ct);
            await audit.LogAsync(
                AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, row.TransferId,
                $"RECONCILED the Sabadell line {row.HoldedBankMovementId} that booked the SEPA payout "
                + $"{Euros(row.Amount)} to {row.IbanMasked} on creditor account {row.SupplierAccountNum}.",
                SepaBookingJobName, row.UserId, nameof(User));
        }

        foreach (var m in movements.Where(NeedsAMatch))
        {
            if (rows.Any(r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                continue;

            var account = RemittanceAccountNum(m.Description);
            if (account is null) continue;

            var matches = UnbookedMatches(unbooked, account.Value, Math.Abs(m.Amount), m.Date);
            if (matches.Count != 1)
            {
                // The page's "bank lines needing a human" panel is where this is surfaced; the sweep
                // only says so once per run in the log.
                logger.LogInformation(
                    "SEPA sweep: Sabadell line {MovementId} matches {Count} unbooked transfer(s) on "
                    + "account {Account} — left for a human.", m.Id, matches.Count, account.Value);
                continue;
            }

            try
            {
                var result = await BookSepaTransferAsync(matches[0].TransferId, m.Id, actorUserId: null);
                if (!result.Succeeded)
                    logger.LogWarning(
                        "SEPA sweep: transfer {TransferId} was not booked against line {MovementId}: {Reason}",
                        matches[0].TransferId, m.Id, result.Message);
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                // One bad line never aborts the sweep.
                logger.LogError(ex,
                    "SEPA sweep: booking transfer {TransferId} against line {MovementId} threw.",
                    matches[0].TransferId, m.Id);
            }
        }
    }

    public async Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId)
    {
        if (BookingUnavailableReason() is { } unavailable)
            return new SepaBookingResult(false, unavailable);

        // The "is it already booked?" read and the stamp that answers it sit either side of several
        // Holded round-trips, so two callers inside that window — a Book click while the sweep runs,
        // or a double-submitted form — would both read "not booked" and both post the whole amount.
        // One server, and a booking takes seconds: serialise them outright.
        await BookingGate.WaitAsync(CancellationToken.None);
        try
        {
            return await BookOneTransferAsync(transferId, bankMovementId, actorUserId);
        }
        finally
        {
            BookingGate.Release();
        }
    }

    private async Task<SepaBookingResult> BookOneTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId)
    {
        // No request-scoped token reaches this method at all — once a payment is posted to Holded the
        // rest of the allocation has to finish (memory/architecture/cancellation-token-propagation.md).
        var ct = CancellationToken.None;

        var transfer = await repo.GetSepaTransferAsync(transferId, ct);
        if (transfer is null)
            return new SepaBookingResult(false, "That transfer no longer exists — reload the page.");

        // Idempotency is the row itself, not a UI state: a second POST of the same form finds
        // BookedAt set and pays nothing.
        if (transfer.BookedAt is not null)
            return new SepaBookingResult(false,
                $"Transfer to {transfer.IbanMasked} is already booked — nothing was posted to Holded.");

        var binding = await repo.GetCreditorContactByUserAsync(transfer.UserId, ct);
        if (binding is null || string.IsNullOrEmpty(binding.HoldedContactId))
            return new SepaBookingResult(false,
                "The member paid by that transfer has no Holded contact binding — bind them on "
                + "/Finance/Creditors first.");

        // The binding is the member's CURRENT one; the transfer names the account the file was built
        // against. A rebind between the two would pay a different creditor account than the bank
        // statement's Ustrd quotes, which is unreconcilable after the fact.
        if (binding.SupplierAccountNum != transfer.SupplierAccountNum)
            return new SepaBookingResult(false,
                $"The member's Holded binding changed since this file was generated (now "
                + $"{binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "unresolved"}, "
                + $"the transfer pays {transfer.SupplierAccountNum}) — book it by hand.");

        // Same account number is not enough: Holded lets two contacts share one 400000xx, so a
        // rebind to a sibling contact on that account slips past the check above. The file paid the
        // contact named on the transfer; anything else pays the wrong recipient's documents. Null on
        // the transfer means a row from before this guard existed — it keeps today's account-only
        // behaviour.
        if (transfer.HoldedContactId is { Length: > 0 }
            && !string.Equals(transfer.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
            return new SepaBookingResult(false,
                $"The member was rebound to a different Holded contact since this file was generated "
                + $"(now {binding.HoldedContactId}, the transfer paid {transfer.HoldedContactId}) — "
                + "book it by hand.");

        // ── Step 1: the bank line. It is the trigger, and its date is every posting's date.
        HoldedBankMovementDto? movement;
        IReadOnlyList<HoldedBankMovementDto> feed;
        try
        {
            feed = await ReadBankFeedAsync(FeedFrom(), ct);
            movement = feed.FirstOrDefault(
                x => string.Equals(x.Id, bankMovementId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not read the Sabadell bank feed to book SEPA transfer {TransferId}.", transferId);
            return new SepaBookingResult(false,
                "The Sabadell bank feed could not be read — nothing was posted.");
        }

        if (movement is null)
            return new SepaBookingResult(false,
                $"That Sabadell line is no longer in the last {FeedWindowDays} days of the feed — "
                + "reload /Finance/Sepa.");

        // ── Step 2: re-validate the pairing here. The posted movement id is never trusted.
        var allRows = await repo.GetSepaPayoutTransferRowsAsync(ct);
        if (PairingRefusal(movement, feed, allRows) is { } pairing)
            return new SepaBookingResult(false, pairing);

        // The file's EndToEndId, on every posting, so a Holded line traces back to one transfer —
        // and so a retry can sum what a crashed run already posted.
        var tag = "SEPA payout E" + transfer.Id.ToString("N");

        // ── Steps 3 and 4: the live creditor balance, and what this transfer already posted.
        decimal owedNow;
        decimal posted;
        try
        {
            // The live chart total, not the nightly mirror: the issue asks what is owed now.
            owedNow = (await client.ListAccountingAccountsAsync(ct))
                .Where(a => a.Number == transfer.SupplierAccountNum)
                .Select(a => -a.Balance)
                .DefaultIfEmpty(0m)
                .First();

            // The entries feed rather than the chart: the chart can omit an entry the feed has, and
            // for this sum omission is the dangerous direction.
            //
            // The window starts at whichever is earlier, the bank line or the day the file was
            // generated, and runs to today: postings made by this flow are dated the bank line, but
            // a pre-#1185 run dated them the *click*, which can be weeks either side of it. Missing
            // one of those would re-post money that already left (nobodies-collective/Humans#1185).
            var generatedOn = allRows.FirstOrDefault(r => r.TransferId == transfer.Id)?.GeneratedAt
                .InZone(MadridZone).Date ?? movement.Date;
            var tagFrom = (generatedOn < movement.Date ? generatedOn : movement.Date)
                .PlusDays(-TagWindowDays);
            var tagTo = Today().PlusDays(TagWindowDays);
            posted = (await client.ListLedgerEntriesAsync(
                    tagFrom, tagTo, transfer.SupplierAccountNum, ct))
                // The account filter is the server's; asserting it here too keeps a dropped query
                // parameter from netting our own two-sided entry to zero and re-posting everything.
                .Where(l => l.AccountNum == transfer.SupplierAccountNum)
                .Where(l => (l.Description ?? "").Contains(tag, StringComparison.Ordinal))
                .Sum(l => l.Debit - l.Credit);   // paying a creditor DEBITS 400xxxxx
            if (posted < 0m) posted = 0m;        // a tagged credit is not "already paid"
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not read Holded's creditor balance to book SEPA transfer {TransferId}.", transferId);
            return new SepaBookingResult(false,
                "Holded's creditor balance could not be read — nothing was posted.");
        }

        // ── Step 5: the arithmetic. The balance refusal applies only to a run that posted nothing:
        // once part of this transfer is provably in Holded, owedNow has already fallen by that much
        // and re-applying the full-amount test would refuse a legitimate resume.
        if (posted <= CentEpsilon && owedNow < transfer.Amount - CentEpsilon)
            return new SepaBookingResult(false,
                $"Creditor account {transfer.SupplierAccountNum} owes {Euros(Math.Max(0m, owedNow))}, "
                + $"less than the {Euros(transfer.Amount)} this transfer pays — nothing was posted.");

        // Rounded to cents once, here: every posting is sent to Holded formatted to two decimals, so
        // an unrounded cap would let each document round up on its own and the total drift over.
        var toPost = Math.Round(
            Math.Min(transfer.Amount - posted, owedNow), 2, MidpointRounding.AwayFromZero);
        var resumed = posted > CentEpsilon;

        var paidDocs = new List<(string DocId, decimal Amount, string Ref)>();
        string? entryRef = null;
        var entryAmount = 0m;
        var remaining = toPost;

        if (remaining > CentEpsilon)
        {
            IReadOnlyList<HoldedPurchaseDocListItemDto> open;
            try
            {
                open = OpenDocs(await client.ListPurchaseDocumentsAsync(ct))
                    .Where(d => string.Equals(d.ContactId, binding.HoldedContactId, StringComparison.Ordinal))
                    // Oldest first: the money the member has been owed longest clears first.
                    .OrderBy(d => d.Date)
                    .ThenBy(d => d.Id, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                logger.LogError(ex, "Could not read Holded purchase documents to book SEPA transfer {TransferId}.", transferId);
                return new SepaBookingResult(false, "Holded's purchase documents could not be read — nothing was posted.");
            }

            // ── Step 6: FIFO document payments, every one dated the bank line. PaymentsPending is
            // read live, so a document an earlier run already paid shows 0 and is skipped — that is
            // what makes this pass idempotent.
            foreach (var doc in open)
            {
                if (remaining <= CentEpsilon) break;
                var amount = Math.Round(
                    Math.Min(remaining, doc.PaymentsPending), 2, MidpointRounding.AwayFromZero);
                if (amount <= 0m) continue;

                try
                {
                    paidDocs.Add((doc.Id, amount, await client.PayPurchaseDocumentAsync(
                        doc.Id, amount, sepa.Value.TreasuryAccountId, movement.Date, tag, ct)));
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    return await RefusedMidBookingAsync(ex, "payment", "Holded document " + doc.Id);
                }

                remaining -= amount;
            }

            // ── Step 7: whatever the documents did not cover — a loan, a balance with no document
            // behind it — as one journal entry: debit 400xxxxx, credit the bank.
            if (remaining > CentEpsilon)
            {
                try
                {
                    entryAmount = remaining;
                    entryRef = await client.PostLedgerEntryAsync(
                        movement.Date, transfer.SupplierAccountNum, sepa.Value.TreasuryLedgerAccount!.Value,
                        remaining, tag, ct);
                    remaining -= entryAmount;   // what is still unposted, which is now nothing
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    entryAmount = 0m;
                    return await RefusedMidBookingAsync(
                        ex, "journal entry", "creditor account " + transfer.SupplierAccountNum);
                }
            }
        }

        // ── Step 7b: the run has to have closed the gap. owedNow caps toPost, so a resume against an
        // account that owes less than the transfer still has to pay posts only part of it — stamping
        // Booked there would claim money that never reached the ledger.
        var shortfall = transfer.Amount - posted - (toPost - remaining);
        if (shortfall > CentEpsilon)
        {
            await AuditBookingAsync(
                $"SHORT SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor "
                + $"account {transfer.SupplierAccountNum} against Sabadell line {movement.Id}: only "
                + $"{Euros(posted + toPost - remaining)} is posted ({PostingSummary()} this run; "
                + $"{Euros(posted)} before it) and the account owes {Euros(Math.Max(0m, owedNow))}. "
                + "The transfer is NOT booked; the next attempt posts only what is still missing.");
            return new SepaBookingResult(false,
                $"Only {Euros(posted + toPost - remaining)} of {Euros(transfer.Amount)} could be "
                + $"posted — creditor account {transfer.SupplierAccountNum} owes "
                + $"{Euros(Math.Max(0m, owedNow))}; the transfer is NOT booked.");
        }

        // ── Step 8: persist, then reconcile, then stamp. Deliberately not the other order — losing
        // the local save after Holded took the money is nobodies-collective/Humans#1185 itself.
        var now = clock.GetCurrentInstant();
        try
        {
            await repo.SaveSepaTransferBookingAsync(transfer.Id, now, actorUserId, movement.Id, null, ct);
        }
        catch (Exception ex)
        {
            // The money has moved and the row does not say so — the one state that must never be
            // invisible. Audit it, then leave the row retryable.
            logger.LogError(ex,
                "Holded accepted every posting for SEPA transfer {TransferId} but the booking row could not be saved.",
                transferId);
            await AuditBookingAsync(
                $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                + $"{movement.Id}: Holded accepted {PostingSummary()} and then the transfer row "
                + "could not be saved. The transfer is NOT booked; the next attempt posts only what "
                + "is still missing.");
            return new SepaBookingResult(false,
                "Holded accepted the postings but the transfer row could not be saved — it is NOT "
                + "marked booked. Retry it: only what is still missing will be posted.");
        }

        var docs = paidDocs
            .Select(p => new HoldedReconcileDocumentRef(p.DocId, HoldedReconcileDocumentType.Purchase))
            .ToList();
        // An unconfirmed entry cannot be named to Holded, but its remainder is still part of the line.
        var entryUnconfirmed = entryRef?.StartsWith("unconfirmed:", StringComparison.Ordinal) == true;
        if (entryRef is { Length: > 0 } && !entryUnconfirmed)
            docs.Add(new HoldedReconcileDocumentRef(entryRef, HoldedReconcileDocumentType.LedgerEntry));

        var reconciledAt = await TryReconcileAsync(movement.Id, movement.Date, docs, entryUnconfirmed, ct);
        if (reconciledAt is not null)
            await repo.MarkSepaTransferReconciledAsync(transfer.Id, reconciledAt.Value, ct);

        // ── Step 9: audit, after the save, naming every Holded id this run created.
        var summary = PostingSummary();
        var resumedPart = resumed ? $"; resumed ({Euros(posted)} already posted)" : "";
        var reconcilePart = reconciledAt is not null
            ? "reconciled."
            : "RECONCILE PENDING — tick it in Holded.";
        var description =
            $"Booked SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
            + $"{transfer.SupplierAccountNum} against Sabadell line {movement.Id} dated "
            + $"{movement.Date.ToInvariantDate()}: {summary}"
            + $"{resumedPart}; {reconcilePart}";

        await AuditBookingAsync(description);

        return new SepaBookingResult(true, $"Booked {Euros(transfer.Amount)}: {summary}.");

        // One booking audit entry, under the acting admin or under the sweep's job name.
        Task AuditBookingAsync(string text) =>
            actorUserId is { } actor
                ? audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, actor, transfer.UserId, nameof(User))
                : audit.LogAsync(
                    AuditAction.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, SepaBookingJobName, transfer.UserId, nameof(User));

        string PostingSummary()
        {
            var docPart = paidDocs.Count == 0
                ? "0 document payment(s)"
                : $"{paidDocs.Count} document payment(s) ("
                  + string.Join(", ", paidDocs.Select(p => $"{p.DocId} {Euros(p.Amount)} {p.Ref}")) + ")";
            var entryPart = entryRef is null
                ? ""
                : $" and a journal entry for {Euros(entryAmount)} ({entryRef})";
            return docPart + entryPart;
        }

        // Whatever Holded already accepted is real money and is kept, but nothing is written to the
        // transfer row: the next attempt re-reads the tag sum and posts only the difference. That
        // resumability is the whole point of nobodies-collective/Humans#1185 — there is no terminal
        // partial state any more.
        async Task<SepaBookingResult> RefusedMidBookingAsync(Exception ex, string what, string where)
        {
            var landed = PostingSummary();
            if (paidDocs.Count > 0)
            {
                // Postings an automation caused, so they get an audit row on this path too — same
                // action as a completed booking, labelled PARTIAL.
                var partial =
                    $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                    + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                    + $"{movement.Id}: Holded accepted {landed} and then refused the {what}. The "
                    + "transfer is NOT booked; the next attempt posts only what is still missing.";
                await AuditBookingAsync(partial);
            }

            logger.LogError(ex,
                "Booking SEPA transfer {TransferId} failed after {Posted} posting(s), on the {What} for {Where}.",
                transferId, paidDocs.Count, what, where);

            if (paidDocs.Count > 0)
                return new SepaBookingResult(false,
                    $"Holded accepted {paidDocs.Count} posting(s) and then refused the {what}; the "
                    + "transfer is NOT marked booked. Retry it — only what is still missing will be posted.");

            // An accepted-but-unreadable posting comes back as an "unconfirmed:" ref rather than an
            // exception, so reaching here on the first one means Holded really refused it.
            return new SepaBookingResult(false, $"Holded refused the {what} — nothing was posted.");
        }

        string? PairingRefusal(
            HoldedBankMovementDto m, IReadOnlyList<HoldedBankMovementDto> lines,
            IReadOnlyList<SepaPayoutTransferRow> all)
        {
            if (m.Amount >= 0m || Math.Abs(m.Amount) != transfer.Amount)
                return $"That Sabadell line is {Euros(m.Amount)}, not the {Euros(transfer.Amount)} this "
                       + "transfer pays — nothing was posted.";
            if (IsReconciled(m))
                return "That Sabadell line is already reconciled in Holded — nothing was posted.";
            // Part of that line is already settled against documents by hand; posting the whole
            // transfer on top of it would pay some of them twice.
            if (!IsUntouched(m))
                return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                       + "Nothing was posted.";

            var account = RemittanceAccountNum(m.Description);
            if (account is null || account.Value != transfer.SupplierAccountNum)
                return $"That Sabadell line's text does not name creditor account "
                       + $"{transfer.SupplierAccountNum} — nothing was posted.";

            // One bank line settles one transfer.
            if (all.Any(r => r.TransferId != transfer.Id
                             && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                return "That Sabadell line already booked another transfer — nothing was posted.";

            var matches = UnbookedMatches(all, account.Value, transfer.Amount, m.Date);
            if (matches.Count > 1)
                return $"{matches.Count} unbooked transfers match that Sabadell line — Humans cannot tell "
                       + "which it paid; settle it in Holded by hand. Nothing was posted.";
            if (matches.Count != 1 || matches[0].TransferId != transfer.Id)
                return "That Sabadell line does not match this transfer — nothing was posted.";

            // The mirror: another unreconciled line of the same amount on the same account, dated
            // no earlier than this file, could equally be the one that paid it.
            var rivals = lines.Count(x =>
                !string.Equals(x.Id, m.Id, StringComparison.Ordinal)
                && NeedsAMatch(x)
                && Math.Abs(x.Amount) == transfer.Amount
                && RemittanceAccountNum(x.Description) == account.Value
                && !all.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                && UnbookedMatches(all, account.Value, transfer.Amount, x.Date)
                    .Any(r => r.TransferId == transfer.Id));
            return rivals == 0
                ? null
                : $"{rivals + 1} Sabadell lines could each have paid this transfer — Humans cannot tell "
                  + "which one did; settle it in Holded by hand. Nothing was posted.";
        }
    }

    /// <summary>Tells Holded the bank line and the postings are the same money. The journal-entry
    /// document type is the one unconfirmed piece of the API, so a refusal that names it is retried
    /// with the purchase documents alone. Whenever the call Holded accepted left a journal remainder
    /// out — that retry, or an entry whose ref is unconfirmed — it only stamps when Holded then
    /// reports the line <c>reconciled</c>, since the remainder can leave it <c>partial</c>. Anything
    /// else leaves the booking standing and the row "reconcile pending", which the page shows and the
    /// sweep re-checks.</summary>
    private async Task<Instant?> TryReconcileAsync(
        string movementId, LocalDate movementDate, List<HoldedReconcileDocumentRef> docs,
        bool entryOmitted, CancellationToken ct)
    {
        if (docs.Count == 0) return null;

        try
        {
            await client.ReconcileBankMovementAsync(sepa.Value.TreasuryAccountId, movementId, docs, ct);
            return entryOmitted
                ? await StampIfReconciledAsync(movementId, movementDate, ct)
                : clock.GetCurrentInstant();
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded refused to reconcile bank movement {MovementId}.", movementId);
        }

        var purchasesOnly = docs
            .Where(d => string.Equals(d.DocumentType, HoldedReconcileDocumentType.Purchase, StringComparison.Ordinal))
            .ToList();
        if (purchasesOnly.Count == 0 || purchasesOnly.Count == docs.Count) return null;

        try
        {
            await client.ReconcileBankMovementAsync(
                sepa.Value.TreasuryAccountId, movementId, purchasesOnly, ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex,
                "Holded refused to reconcile bank movement {MovementId} against its purchase documents either.",
                movementId);
            return null;
        }

        return await StampIfReconciledAsync(movementId, movementDate, ct);
    }

    /// <summary>The stamp for a reconcile that left a journal remainder out. That remainder is part of
    /// this very line, so Holded accepting the rest can leave the line <c>partial</c>; stamping
    /// ReconciledAt there would audit it as reconciled and drop the row out of the sweep's pending
    /// re-check, hiding the unmatched remainder. ReconciledAt means Holded says <c>reconciled</c> — so
    /// ask it; anything else (including a feed the re-read cannot get) stays reconcile pending.</summary>
    private async Task<Instant?> StampIfReconciledAsync(
        string movementId, LocalDate movementDate, CancellationToken ct)
    {
        HoldedBankMovementDto? line;
        try
        {
            var after = await client.ListBankMovementsAsync(
                sepa.Value.TreasuryAccountId, movementDate, movementDate, ct);
            line = after.FirstOrDefault(m => string.Equals(m.Id, movementId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex,
                "Could not re-read bank movement {MovementId} after reconciling it; the transfer stays "
                + "reconcile pending.", movementId);
            return null;
        }

        if (line is not null && IsReconciled(line)) return clock.GetCurrentInstant();

        logger.LogInformation(
            "Holded took the reconcile for bank movement {MovementId} without the journal remainder but "
            + "the line reads {Status}; the transfer stays reconcile pending.", movementId,
            line?.Status ?? "gone");
        return null;
    }

    private LocalDate Today() => clock.GetCurrentInstant().InZone(MadridZone).Date;

    /// <summary>The first day the feed is read from for matching: the oldest non-stale file's first
    /// payable day, so every line <see cref="UnbookedMatches"/> could accept is on it.</summary>
    private LocalDate FeedFrom() => Today().PlusDays(-FeedWindowDays - GenerationSlackDays);

    private Task<IReadOnlyList<HoldedBankMovementDto>> ReadBankFeedAsync(
        LocalDate from, CancellationToken ct) =>
        client.ListBankMovementsAsync(sepa.Value.TreasuryAccountId, from, Today(), ct);

    /// <summary>An outgoing line Holded has not started reconciling — the only kind that can still
    /// be a SEPA payout waiting to be booked. <c>partial</c> counts as started: somebody has already
    /// settled part of that line against documents by hand, so posting the whole transfer on top of
    /// it would pay those documents twice.</summary>
    private static bool NeedsAMatch(HoldedBankMovementDto m) => m.Amount < 0m && IsUntouched(m);

    /// <summary>Holded reports <c>pending</c> | <c>partial</c> | <c>reconciled</c>; only the first
    /// has nothing of ours or anyone else's already tied to it.</summary>
    private static bool IsUntouched(HoldedBankMovementDto m) =>
        string.Equals(m.Status, PendingStatus, StringComparison.Ordinal);

    private static bool IsReconciled(HoldedBankMovementDto m) =>
        string.Equals(m.Status, ReconciledStatus, StringComparison.Ordinal);

    /// <summary>The 400000xx the payout file wrote into the remittance text, or null when the line
    /// is not one of ours.</summary>
    private static int? RemittanceAccountNum(string? description)
    {
        var match = RemittanceAccount.Match(description ?? "");
        return match.Success
            && int.TryParse(match.Groups["acct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
            ? num
            : null;
    }

    /// <summary>The unbooked transfers a bank line dated <paramref name="lineDate"/> for
    /// <paramref name="account"/> and <paramref name="amount"/> could be paying. More than one is
    /// ambiguous and waits for a human. Only files generated inside the feed window count: a stale
    /// row from an old file would otherwise make every later transfer of the same amount permanently
    /// ambiguous, and no live bank line can be paying it anyway. A line dated before the file that
    /// asked for it cannot be paying it either — that one is some older payment of the same amount,
    /// still unreconciled on the feed.</summary>
    private List<SepaPayoutTransferRow> UnbookedMatches(
        IReadOnlyList<SepaPayoutTransferRow> rows, int account, decimal amount, LocalDate lineDate) =>
        rows.Where(r => r.BookedAt is null && r.SupplierAccountNum == account && r.Amount == amount
                        && !IsStale(r) && lineDate >= FirstPayableDate(r))
            .ToList();

    /// <summary>The earliest bank date that can belong to a transfer: its file's generation day,
    /// less <see cref="GenerationSlackDays"/>.</summary>
    private LocalDate FirstPayableDate(SepaPayoutTransferRow row) =>
        row.GeneratedAt.InZone(MadridZone).Date.PlusDays(-GenerationSlackDays);

    /// <summary>A transfer whose file was generated before the bank feed's window — no line the feed
    /// still returns can book it, so it is a human's job in Holded.</summary>
    private bool IsStale(SepaPayoutTransferRow row) =>
        row.GeneratedAt.InZone(MadridZone).Date < Today().PlusDays(-FeedWindowDays);

    /// <summary>Why booking is unavailable for every row at once, or null. The organisation's SEPA
    /// identity is required because it is what generated the transfers; the treasury account because
    /// omitting it lets Holded pay from whichever account it defaults to; its ledger account because
    /// the journal entry for an uncovered remainder has to credit the same bank.</summary>
    private string? BookingUnavailableReason()
    {
        if (GetSepaPayoutSettings().UnavailableReason is { } sepaMissing) return sepaMissing;
        if (string.IsNullOrWhiteSpace(sepa.Value.TreasuryAccountId))
            return "Booking is unavailable — Sepa:TreasuryAccountId is not configured.";
        return sepa.Value.TreasuryLedgerAccount is null
            ? "Booking is unavailable — Sepa:TreasuryLedgerAccount is not configured."
            : null;
    }

    /// <summary>Approved purchase documents that still owe something. A draft books nothing to the
    /// ledger, so paying one would post against a document that does not exist for accounting.</summary>
    private static List<HoldedPurchaseDocListItemDto> OpenDocs(
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs) =>
        docs.Where(d => d.IsDraft == false && d.PaymentsPending > 0m).ToList();

    private static string Euros(decimal amount) =>
        amount.ToString("F2", CultureInfo.InvariantCulture) + " EUR";

    /// <summary>The organisation's name, reduced to something safe in a download filename.</summary>
    private static string FileSlug(string name)
    {
        var cleaned = new string(SepaText.Normalize(name, 32).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray());
        var slug = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "sepa" : slug;
    }

    // ─── GDPR (Article 15 export) ───────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        var payouts = await repo.GetSepaPayoutsForUserAsync(userId, ct);
        return
        [
            new UserDataSlice(HoldedCreditorAccount,
                binding is null
                    ? null
                    : new
                    {
                        binding.SupplierAccountNum,
                        binding.HoldedContactId,
                        Source = binding.Source.ToString(),
                    }),

            // Every credit transfer paid to them. The IBAN is the masked one, as everywhere
            // outside the file and the payout row itself.
            new UserDataSlice(SepaPayouts, payouts.Select(p => new
            {
                p.GeneratedAt,
                p.FileName,
                p.SupplierAccountNum,
                p.HoldedContactId,
                p.CreditorName,
                Iban = p.IbanMasked,
                p.Amount,
                p.BookedAt,
                p.HoldedBankMovementId,
                p.ReconciledAt,
            })),
        ];
    }

    // ─── GDPR (Article 17 erasure) ─────────────────────────────────────────────

    private const string PayoutRetention =
        "Retained in full, nothing erased: a payout file is the credit-transfer order the bank was " +
        "given, and it keeps the payee's legal name, their IBAN (stored unmasked in both the transfer " +
        "row and the file's XML — the export masks it, these do not), the creditor account and the " +
        "amount. Spanish law requires the books and their supporting documents be kept 6 years " +
        "(Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66), and a " +
        "payment order stripped of its payee is no longer evidence of the payment. GDPR Art. 17(3)(b).";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HoldedCreditorAccount] = null,
            [SepaPayouts] = PayoutRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Drops the member↔Holded creditor binding. The invoices themselves live in
    /// Holded and are fiscal records outside this section's ownership; the payout files
    /// this section does own survive on the same basis — see <see cref="PayoutRetention"/>.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        ClearCreditorContactAsync(userId, ct);

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

        return baseTag + categoryId.ToString("N")[..4];
    }
}
