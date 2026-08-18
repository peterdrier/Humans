using Humans.Application;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// The creditor half of Finance: the member to 400000xx account binding, and the balance,
/// statement and payment figures derived from the Holded section's ledger mirror.
/// </summary>
internal sealed class CreditorService(
    IHoldedRepository repo,
    IHoldedClient client,
    // The ledger mirror moved to the Holded section; all line/balance reads go through its contract.
    IHoldedService holded,
    IClock clock,
    IMemoryCache cache,
    ILogger<CreditorService> logger) : ICreditorAdminService, IUserDataContributor
{
    private static readonly TimeSpan ContactsCacheDuration = TimeSpan.FromMinutes(2);

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
        var debits = lines.Where(l => l.Debit > 0m).ToList();

        return new HoldedCreditorStatus(
            SupplierAccountNum: num,
            Balance: balance,
            OwedToMember: OwedFrom(balance),
            LastPaymentDate: debits.Count == 0 ? null : debits.Max(l => l.Date).InZone(Madrid).Date,
            TotalPaid: debits.Sum(l => l.Debit));
    }

    // ── Ledger derivations (sign confirmed against live data: Daniela 40000001
    //    credit 12720 − debit 9540 = 3180 owed; chart showed −3180) ──────────────
    //    balance = Σdebit − Σcredit (negative = org owes); owed = max(0, −balance); payments = debit lines.

    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLineInfo> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    private static decimal OwedFrom(decimal? balance) => Math.Max(0m, -(balance ?? 0m));

    // ─── Creditor bindings + statement ──────────────────────────────────────────

    public async Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts,
                       IReadOnlyList<CreditorContactBinding> Unresolved)> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        var byAccount = await holded.GetAccountBalancesAsync(ct: ct);

        // Holded is the only place the chart-account label lives — nothing caches it locally.
        // Range filter is load-bearing: Holded assigns a supplier number to every supplier contact,
        // so an ordinary org vendor would otherwise become a bindable "creditor account" here.
        // Group-by-first: a duplicate account number must not throw the whole list.
        var contacts = (await ListContactsOrEmptyAsync(ct))
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax)
            .GroupBy(c => c.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Rows are decided by the contact's 400000xx as Holded reports it, falling back to the number
        // cached on the binding. Resolving through the contact is load-bearing twice over — see the
        // ListCreditorAccountsAsync invariant in Docs/Finance.md.
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
        // The mirror now spans the whole chart, so its balances are range-filtered here.
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
                    OwedToMember: OwedFrom(balance),
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

    /// <summary>Holded's contact list, cached for <see cref="ContactsCacheDuration"/>
    /// (design-rules §15 Option A). A vendor failure costs the account names, not the page; anything
    /// that is not a vendor failure is a bug and throws.</summary>
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
    // A 400000xx account, and the Holded contact behind it, binds to at most one member. Every write
    // path checks with FindConflictingBinding; a manual bind refuses, the automatic paths record and
    // log. Why they differ, and why this is not a unique index, are in Docs/Finance.md.

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

    /// <summary>True when the member's creditor row is still exactly what <paramref name="asRead"/>
    /// holds — same row, same content, or still absent when nothing was read. Row identity alone is not
    /// enough (upsert is keyed by UserId and mutates in place), and neither is absence.</summary>
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

        // A binding whose 400000xx never resolved carries the contact and no number, so the check above
        // cannot see it. Name the contact — /Finance/Creditors keys on the number and will not show it.
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
        // Lines come from the mirror — no Holded call. The contact header rides the same 2-minute
        // cached contact list /Finance/Creditors already reads, so a warm page costs nothing either.
        var lines = await holded.GetLedgerLinesAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        var contact = (await ListContactsOrEmptyAsync(ct))
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);

        var balance = LedgerBalance(lines);
        return new HoldedCreditorLedger(
            SupplierAccountNum: supplierAccountNum,
            Name: contact?.Name,
            Balance: balance,
            OwedToMember: OwedFrom(balance),
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

        // The seed is our own cache off a prior report, not something Holded just told us, so it gets
        // the manual bind's treatment: refused, and the push mints this member their own contact.
        // This is also what keeps Unbind durable — see the seed-refusal invariant in Docs/Finance.md.
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

        // Already bound to this contact with the number resolved: the write would change only
        // UpdatedAt, which nothing reads, and it sits past a multi-second Holded round-trip holding a
        // stale copy — so it would resurrect a binding an admin cleared mid-push. Skipping it is what
        // makes Unbind hold in the steady state.
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        // A binding still missing its number writes real content, so it cannot be skipped — but it can
        // still undo an admin's action from the copy read before the round-trip. Re-read right before
        // writing (nobodies-collective/Humans#995; no version column, no-concurrency-tokens.md).
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

        // Holded just told us this number belongs to the contact we pushed against, so it is written
        // either way; only the contact-id overlap is already covered upstream by EnsureCreditorContact.
        var conflict = FindConflictingBinding(
            await repo.GetCreditorContactsAsync(ct), userId, supplierAccountNum, holdedContactId: null);
        if (conflict is not null)
            LogBindingCollision(
                nameof(SetCreditorAccountNumAsync), userId, binding.HoldedContactId,
                supplierAccountNum, conflict);

        // Same guard as EnsureCreditorContactAsync: the row below carries the binding read above, so
        // writing it over an admin's newer one reverts it wholesale (nobodies-collective/Humans#995).
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

}
