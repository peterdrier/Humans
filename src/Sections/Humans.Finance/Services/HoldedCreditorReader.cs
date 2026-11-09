using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using NodaTime;
using static Humans.Finance.Services.HoldedCreditorBindings;

namespace Humans.Finance.Services;

internal sealed class HoldedCreditorReader(
    IHoldedRepository repo,
    IHoldedService holded,
    IExpenseHoldedBookingService expenses,
    SepaBankBooking booking,
    ILogger<Service> logger) : IApplicationService
{
    internal const string ContactListUnavailable =
        "The Holded contact mirror has never synced, so no member's creditor account is known. Run Sync now on "
        + "/Holded.";
    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    internal async Task<CreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default)
    {
        if (supplierAccountNum is not { } num)
            return null;

        var lines = await holded.GetLedgerLinesAsync(num, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        var payments = lines.Where(l => l.Debit > 0m).ToList();

        return new CreditorStatus(
            SupplierAccountNum: num,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            LastPaymentDate: payments.Count == 0
                ? null
                : payments.Max(l => l.Date).InZone(MadridZone).Date,
            TotalPaid: payments.Sum(l => l.Debit));
    }

    // Balance = Σdebit − Σcredit, so negative means the organisation owes the member.
    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLineInfo> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    internal async Task<(IReadOnlyList<CreditorAccountRow> Accounts,
                       IReadOnlyList<CreditorContactBinding> Unresolved,
                       string? UnavailableReason)> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        // Not range-filtered: the range decides which accounts are creditor accounts, not which contact a binding names.
        if (await ContactsByIdAsync(ct) is not { } contactById)
            return ([], [], ContactListUnavailable);

        var byAccount = await holded.GetAccountBalancesAsync(ct: ct);

        // The range filter is load-bearing: Holded numbers every supplier contact, so unfiltered an org vendor becomes
        // bindable. Group-by-first so a duplicate number cannot throw.
        var contacts = contactById.Values
            .Where(c => IsMemberCreditorAccount(c.SupplierAccountNum))
            .GroupBy(c => c.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Holded's contact, never the binding's stored number, decides the row, so two bindings on one contact show as a
        // collision. One partition of one snapshot, so no binding is both on a row and on the unresolved card.
        var resolved = (await repo.GetCreditorContactsAsync(ct))
            .Select(b => (Account: ResolveCreditorAccount(b, contactById), Binding: b))
            .ToList();

        // Every binding, not the first: only UserId is unique, and the automatic paths record what Holded assigned
        // rather than refusing, so a second member on one 400000xx is what an admin must see here.
        var bindings = resolved
            .Where(x => x.Account is not null)
            .GroupBy(x => x.Account!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CreditorContactBinding>)g
                    .Select(x => ToBinding(x.Binding))
                    .ToList());

        // The contact is missing from Holded's list or has no creditor number. Nothing retries these, so they are
        // returned, not dropped: unreturned is unbindable.
        var unresolved = resolved
            .Where(x => x.Account is null)
            .Select(x => ToBinding(x.Binding))
            .ToList();

        // From the purchase-document mirror, as stale as the ledger balances beside it. Same OpenDocs filter the
        // booking applies. The docs also feed the duplicate check: an inflated balance makes the row unpayable, and
        // null (never synced) is refused too. Open counts stay unknown until every doc carries its payments pending.
        Dictionary<string, int>? openDocsByContact = null;
        HashSet<Guid>? flaggedUsers = null;
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        if (mirror.LastSyncAt is not null)
        {
            var purchaseDocs = mirror.Docs.Select(ToListItem).ToList();
            if (mirror.Docs.All(d => d.PaymentsPending is not null))
                openDocsByContact = SepaBankBooking.OpenDocs(purchaseDocs)
                    .Where(d => !string.IsNullOrEmpty(d.ContactId))
                    .GroupBy(d => d.ContactId!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            flaggedUsers = (await FindDuplicateDocumentsAsync(purchaseDocs, contactById, ct))
                .Select(m => m.Booking.CreditorUserId)
                .ToHashSet();
        }

        // An unbooked transfer means the balance is still open in Holded, so paying again would pay twice. A withdrawn
        // transfer releases the row, as does one too old for the feed to book (a human's to settle by then).
        var pendingFileByAccount = (await repo.GetSepaPayoutTransferRowsAsync(ct))
            .Where(r => !r.IsBooked && !r.IsWithdrawn && !booking.IsStale(r))
            .GroupBy(r => r.SupplierAccountNum)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.GeneratedAt).First().FileName);

        // Plus bound accounts with no lines yet and every Holded creditor contact: a first-time submitter's account
        // exists before any activity. The mirror spans the whole chart, so balances are range-filtered here.
        var rows = byAccount.Keys
            .Where(num => IsMemberCreditorAccount(num))
            .Union(bindings.Keys).Union(contacts.Keys)
            .Select(num =>
            {
                decimal? balance = byAccount.TryGetValue(num, out var b) ? b : null;
                bindings.TryGetValue(num, out var bound);

                // Holded can put two contacts on one 400000xx, so a singly-bound row takes its name and IBAN from the
                // bound contact: that is who a payout would pay. Other rows are unpayable and keep the account's.
                var contact = bound is { Count: 1 }
                    ? contactById.GetValueOrDefault(bound[0].HoldedContactId)
                    : contacts.GetValueOrDefault(num);

                var row = new CreditorAccountRow(
                    SupplierAccountNum: num,
                    Name: contact?.Name ?? "",
                    Balance: balance,
                    OwedToMember: balance is { } bal ? Math.Max(0m, -bal) : 0m,
                    Bindings: bound ?? [],
                    IbanMasked: string.IsNullOrWhiteSpace(contact?.Iban)
                        ? null
                        : IbanFormatter.Mask(contact.Iban),
                    OpenDocumentCount: bound is { Count: 1 } && openDocsByContact is not null
                        ? openDocsByContact.GetValueOrDefault(bound[0].HoldedContactId)
                        : null,
                    PendingPayoutFileName: pendingFileByAccount.GetValueOrDefault(num),
                    HasDuplicateDocuments: bound is { Count: 1 }
                        ? flaggedUsers?.Contains(bound[0].UserId)
                        : false);
                return row with { NotPayableReason = GetNotPayableReason(row) };
            }).ToList();

        // Accounts but no names at all means an unusable contact list, leaving the bind card as bare numbers. One
        // missing name is a real gap in Holded, so only the all-or-nothing case is logged.
        if (rows.Count > 0 && rows.TrueForAll(r => string.IsNullOrWhiteSpace(r.Name)))
            logger.LogWarning(
                "None of the {Count} creditor accounts resolved a Holded contact name; the bind card and " +
                "/Finance/Creditors will show bare account numbers.", rows.Count);

        return (rows, unresolved, null);
    }

    /// <summary>The list shape the booking filter and duplicate matcher read.</summary>
    internal static HoldedPurchaseDocListItemDto ToListItem(HoldedPurchaseDocInfo d) => new()
    {
        Id = d.Id,
        DocNumber = d.DocNumber,
        ContactId = d.ContactId,
        ContactName = d.ContactName,
        Description = d.Description,
        Date = d.Date.AtStartOfDayInZone(MadridZone).ToInstant(),
        Subtotal = d.Subtotal,
        Tax = d.Tax,
        Total = d.Total,
        PaymentsPending = d.PaymentsPending ?? 0m,
        IsDraft = !d.IsApproved,
        Currency = d.Currency,
        Tags = d.Tags,
    };

    private static string? GetNotPayableReason(CreditorAccountRow row)
    {
        var account = $"Creditor account {row.SupplierAccountNum}";
        return row.Bindings.Count switch
        {
            0 => $"{account} is not bound to a member — bind it first.",
            > 1 => $"{account} is bound to {row.Bindings.Count} members — unbind all but the one who owns it first.",
            _ when row.HasDuplicateDocuments is null =>
                $"{account}: the Holded purchase-document mirror has never synced, so duplicate documents cannot be ruled out — run Sync now on /Holded.",
            _ when row.HasDuplicateDocuments == true =>
                $"{account} has duplicate documents in Holded — remove them on /Finance/HoldedDuplicates first.",
            _ when row.PendingPayoutFileName is { } file =>
                $"{account} is already in payout file {file}, which is not booked yet — book it or withdraw the transfer first.",
            _ when row.OwedToMember <= 0m => $"{account}: nothing owed.",
            _ when row.IbanMasked is null => $"{account} has no IBAN on the Holded contact it is bound to.",
            _ => null,
        };
    }

    /// <summary>The account Holded's contact is on. Never the binding's stored number: it can be stale, and a stale
    /// number pays, names or exports the wrong account.</summary>
    internal static int? ResolveCreditorAccount(
        HoldedCreditorContact binding, IReadOnlyDictionary<string, HoldedContactDto> contacts)
    {
        var account = contacts.GetValueOrDefault(binding.HoldedContactId)?.SupplierAccountNum;
        return IsMemberCreditorAccount(account) ? account : null;
    }

    /// <summary>Keyed by contact id, the identity a binding stores. The rows and the payout both resolve through this
    /// one map, so a row's name and IBAN cannot disagree with the file's. From the contact mirror; null when it has
    /// never synced.</summary>
    internal async Task<Dictionary<string, HoldedContactDto>?> ContactsByIdAsync(CancellationToken ct) =>
        await holded.GetContactsAsync(ct) is not { LastSyncAt: not null } mirror
            ? null
            : mirror.Contacts
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    internal async Task<CreditorLedger?> GetCreditorLedgerAsync(
        int supplierAccountNum, CancellationToken ct = default)
    {
        // Mirror lines and the cached contact list: no Holded call.
        var lines = await holded.GetLedgerLinesAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        // Unreadable, the member on the account is unknown: the statement says so rather than guess from a stored number.
        if (await ContactsByIdAsync(ct) is not { } contacts)
            return Ledger(supplierAccountNum, lines, null, ContactListUnavailable);

        var bindings = (await repo.GetCreditorContactsAsync(ct))
            .Where(b => ResolveCreditorAccount(b, contacts) == supplierAccountNum)
            .ToList();
        HoldedContactDto? contact = null;
        if (bindings.Count == 1)
        {
            contact = contacts.GetValueOrDefault(bindings[0].HoldedContactId);
        }
        else if (bindings.Count == 0)
        {
            var candidates = contacts.Values.Where(c => c.SupplierAccountNum == supplierAccountNum).ToList();
            if (candidates.Count == 1) contact = candidates[0];
        }

        return Ledger(supplierAccountNum, lines, contact, null);
    }

    private static CreditorLedger Ledger(
        int supplierAccountNum, IReadOnlyList<HoldedLedgerLineInfo> lines, HoldedContactDto? contact,
        string? contactUnavailableReason)
    {
        var balance = LedgerBalance(lines);
        // Accumulates oldest first from the account's first line (credit = owed to the member); the lines keep the
        // mirror's order.
        var runningBalances = new decimal[lines.Count];
        var running = 0m;
        foreach (var i in Enumerable.Range(0, lines.Count)
                     .OrderBy(i => lines[i].Date)
                     .ThenBy(i => lines[i].EntryNumber)
                     .ThenBy(i => lines[i].Line))
            runningBalances[i] = running += lines[i].Credit - lines[i].Debit;
        return new CreditorLedger(
            SupplierAccountNum: supplierAccountNum,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            Lines: lines.Select((l, i) => new CreditorLedgerLine
            {
                EntryNumber = l.EntryNumber,
                Line = l.Line,
                Date = l.Date,
                AccountNum = l.AccountNum,
                Debit = l.Debit,
                Credit = l.Credit,
                Type = l.Type,
                Description = l.Description,
                RunningBalance = runningBalances[i],
            }).ToList(),
            Contact: contact is null
                ? null
                : new CreditorContactInfo(
                    contact.Name, contact.TradeName, contact.Email, contact.Phone,
                    contact.Mobile, contact.Iban, contact.TaxCode, contact.Address),
            ContactUnavailableReason: contactUnavailableReason);
    }

    private async Task<IReadOnlyList<HoldedDuplicateMatcher.Match>> FindDuplicateDocumentsAsync(
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs,
        IReadOnlyDictionary<string, HoldedContactDto> contactById, CancellationToken ct)
    {
        var bookings = await expenses.GetHoldedBookingsAsync(ct);
        if (bookings.Count == 0) return [];
        var bindings = await repo.GetCreditorContactsAsync(ct);
        return HoldedDuplicateMatcher.Find(bookings, docs, MemberContacts(bookings, bindings, contactById));
    }

    /// <summary>The bound contact, the ones their reports name, and any same-named creditor-range contact bound to
    /// nobody else: the race created a second contact for some members.</summary>
    internal static Dictionary<Guid, IReadOnlySet<string>> MemberContacts(
        IReadOnlyList<ExpenseHoldedBooking> bookings, IReadOnlyList<HoldedCreditorContact> bindings,
        IReadOnlyDictionary<string, HoldedContactDto> contactById) =>
        HoldedDuplicateMatcher.MemberContacts(
            bookings.Select(b => (b.CreditorUserId, b.HoldedContactId)), bindings,
            id => contactById.GetValueOrDefault(id)?.Name,
            contactById.Values
                .Where(c => IsMemberCreditorAccount(c.SupplierAccountNum)
                            && !string.IsNullOrWhiteSpace(c.Name))
                .ToLookup(c => c.Name!.Trim(), c => c.Id, StringComparer.OrdinalIgnoreCase));

}
