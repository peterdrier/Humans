using Humans.Base.Caching;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Application-layer service for the Holded finance integration.
/// Manages account provisioning, purchase-doc sync, actuals computation, and unmatched reporting.
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
    ILogger<Service> logger) : IHoldedFinanceService, IUserDataContributor
{
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
            var draftIds = await client.ListDraftPurchaseIdsAsync(ct);

            var docs = allDocs.Select(doc => MapDoc(doc, entries, draftIds, now)).ToList();

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
        IReadOnlySet<string> draftIds,
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
            IsApproved = !draftIds.Contains(doc.Id),
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
            .Select(g => new HoldedActualRow(g.Key, g.Sum(d => d.Total)))
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
                // TODO(probe): confirm Holded deep-link URL format
                $"https://app.holded.com/purchases/{d.HoldedDocId}"))
            .ToList();
    }

    public async Task<string?> GetHoldedAccountIdForCategoryAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        return map.FirstOrDefault(m => m.IsActive && m.BudgetCategoryId == budgetCategoryId)?.HoldedAccountId;
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

    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 40000999;

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

        // Holded is the only place the account label lives. The range filter is load-bearing: Holded
        // numbers every supplier contact, so unfiltered, an org vendor becomes a bindable creditor
        // account. Group-by-first so a duplicate number cannot throw the whole list.
        var contacts = (await ListContactsOrEmptyAsync(ct))
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
                contacts.TryGetValue(num, out var contact);
                return new HoldedCreditorAccountRow(
                    SupplierAccountNum: num,
                    Name: contact?.Name ?? "",
                    Balance: balance,
                    OwedToMember: balance is { } bal ? Math.Max(0m, -bal) : 0m,
                    Bindings: bound ?? []);
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

        // A refused seed cannot collide, so anything left is a pre-existing overlap on the member's own
        // binding — not this push's doing, but not to be carried forward unreported either.
        var accountNum = binding?.SupplierAccountNum ?? seedAccountNum;
        var conflict = FindConflictingBinding(allBindings, userId, accountNum, contactId);
        if (conflict is not null)
            LogBindingCollision(
                nameof(EnsureCreditorContactAsync), userId, contactId, accountNum, conflict);

        // A member who already holds this contact has nothing to write but UpdatedAt, which nothing
        // reads — and this write sits on the far side of a multi-second Holded round-trip, holding a
        // copy read before it. Skipping it is what makes Unbind hold against an in-flight push.
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        // A binding still missing its number writes real content, so it cannot be skipped — but it can
        // still undo an admin's Bind or Unbind from the copy read before the round-trip. Re-reading
        // here shrinks that window without a version column (nobodies-collective/Humans#995).
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
        // (nobodies-collective/Humans#995). No I/O since that read, so the window is sub-millisecond.
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
