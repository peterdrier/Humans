using static Humans.Finance.Services.HoldedCreditorReader;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
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
using Microsoft.Extensions.Options;
using NodaTime;
using static Humans.Finance.Services.SepaText;
using static Humans.Finance.Services.HoldedCreditorBindings;

namespace Humans.Finance.Services;

internal sealed class Service(
    IHoldedRepository repo,
    IBudgetService budget,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IExpenseHoldedBookingService expenses,
    IClock clock,
    IAuditLogService audit,
    IUserService users,
    IEmailService emailService,
    FinanceEmails emails,
    IOptions<SepaOptions> sepa,
    TrackedCache<string, Dictionary<string, int>> bookingState,
    IPaymentDetailsManagement paymentDetails,
    ILogger<Service> logger) : IFinanceService, IFinanceWrite, IFinanceAdminService, IUserDataContributor
{
    public Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default) =>
        paymentDetails.GetIbanAsync(userId, ct);

    public Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default) =>
        paymentDetails.SetIbanAsync(userId, iban, ct);

    internal const string HoldedCreditorAccount = HoldedCreditorBindings.HoldedCreditorAccount;
    internal const string AdditionalHoldedCreditorAccounts = "AdditionalHoldedCreditorAccounts";
    internal const string SepaPayouts = "SepaPayouts";
    internal const string CreditorLedger = "CreditorLedger";
    internal const string HoldedCreditorContacts = "HoldedCreditorContacts";

    internal const string HoldedExpenseAccount = HoldedCategoryAccounts.HoldedExpenseAccount;

    internal const string PurchaseMirrorUnavailable =
        "The Holded purchase-document mirror has never synced, so no duplicate can be judged. Run Sync now on /Holded.";

    internal const string ContactListUnavailable = HoldedCreditorReader.ContactListUnavailable;
    private static readonly DateTimeZone MadridZone = HoldedCreditorReader.MadridZone;
    private HoldedCreditorReader CreditorReader => new(repo, holded, expenses, booking, logger);
    private readonly HoldedCreditorBindings creditorBindings = new(repo, holded, holdedWrite, clock, audit, logger);
    private readonly HoldedCategoryAccounts categoryAccounts = new(repo, holded, holdedWrite, budget, clock, audit, logger);
    private readonly SepaBankBooking booking = new(repo, holded, holdedWrite, clock, bookingState, audit, sepa, logger);
    private readonly SepaPayoutEmails payoutEmails = new(users, emailService, emails, logger);

    public Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default) =>
        categoryAccounts.GetProvisioningPlanAsync(blockStart, ct);

    public Task<int> ProvisionAsync(int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default) =>
        categoryAccounts.ProvisionAsync(blockStart, addAll, actorUserId, ct);

    public Task<ExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default) =>
        categoryAccounts.CreateOrLinkExpenseAccountAsync(name, existingAccountNum, ct);

    public Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default) =>
        categoryAccounts.SetExpenseAccountActiveAsync(accountNum, isActive, ct);

    public Task<IReadOnlyList<ExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default) =>
        categoryAccounts.ListExpenseAccountsAsync(activeOnly, ct);

    /// <summary>The expense lines against the ledger mirror, with SEPA rows. The one non-mirror read is the cached
    /// contact list, which says which account each bound contact is on. Unreadable, or missing a bound contact, the report is unavailable: the
    /// binding's stored number may be wrong, and judging against it is worse than not judging. The document mirror is
    /// not read.</summary>
    public async Task<ExpenseReconReport> GetExpenseReconAsync(int? year, CancellationToken ct = default)
    {
        // A push settled after the last good sweep is Pending, not Missing.
        if (await holded.GetLedgerSyncedAtAsync(ct) is not { } syncedAt)
            return ExpenseRecon.Unavailable(year, "The Holded ledger mirror has never synced.");

        var balances = await holded.GetAccountBalancesAsync(ct);

        if (await CreditorReader.ContactsByIdAsync(ct) is not { } contacts)
            return ExpenseRecon.Unavailable(year, ContactListUnavailable);

        // The list skips a contact it cannot parse, so a non-empty list can still be missing a bound member's account.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var unreadable = bindings.Count(b => !contacts.ContainsKey(b.HoldedContactId));
        if (unreadable > 0)
            return ExpenseRecon.Unavailable(year,
                $"{unreadable} bound Holded contact(s) are missing from the contact list, so those members' accounts are not known.");

        var expected = await expenses.GetHoldedReconLinesAsync(ct);
        var accountByUser = bindings
            .Select(b => (b.UserId, Account: contacts.GetValueOrDefault(b.HoldedContactId)?.SupplierAccountNum))
            .Where(x => IsMemberCreditorAccount(x.Account))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.First().Account!.Value);

        var ledger = new Dictionary<int, IReadOnlyList<HoldedLedgerLineInfo>>();
        foreach (var account in balances.Keys
                     .Where(n => IsMemberCreditorAccount(n))
                     .Union(accountByUser.Values))
            ledger[account] = await holded.GetLedgerLinesAsync(account, ct);

        var transfers = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var members = await users.GetUserInfosAsync(expected.Select(l => l.CreditorUserId).ToHashSet(), ct);
        return ExpenseRecon.Build(
            year, syncedAt, expected, accountByUser, ledger, transfers,
            id => members.GetValueOrDefault(id)?.BurnerName ?? id.ToString());
    }

    public Task<IReadOnlyList<CategoryActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default) =>
        categoryAccounts.GetActualsForYearAsync(calendarYear, ct);

    public Task<IReadOnlyList<UnmatchedDocRow>> GetUnmatchedAsync(CancellationToken ct = default) =>
        categoryAccounts.GetUnmatchedAsync(ct);

    public Task<string?> GetHoldedAccountIdForCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default) =>
        categoryAccounts.GetHoldedAccountIdForCategoryAsync(budgetCategoryId, ct);

    public Task<IReadOnlyList<CategoryAccountRow>> GetCategoryMapAsync(CancellationToken ct = default) =>
        categoryAccounts.GetCategoryMapAsync(ct);

    public Task<IReadOnlyList<HoldedManagedAccountVm>> GetManagedAccountsAsync(CancellationToken ct = default) =>
        categoryAccounts.GetManagedAccountsAsync(ct);

    public Task<CreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default) =>
        CreditorReader.GetCreditorStatusAsync(supplierAccountNum, ct);

    public Task<(IReadOnlyList<CreditorAccountRow> Accounts,
                 IReadOnlyList<CreditorContactBinding> Unresolved,
                 string? UnavailableReason)> ListCreditorAccountsAsync(CancellationToken ct = default) =>
        CreditorReader.ListCreditorAccountsAsync(ct);

    public Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default) =>
        creditorBindings.GetCreditorContactByUserAsync(userId, ct);

    public Task<CreditorBindResult> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, Guid actorUserId, CancellationToken ct = default) =>
        creditorBindings.SetCreditorContactAsync(userId, supplierAccountNum, actorUserId, ct);

    public Task<CreditorLedger?> GetCreditorLedgerAsync(
        int supplierAccountNum, CancellationToken ct = default) =>
        CreditorReader.GetCreditorLedgerAsync(supplierAccountNum, ct);

    public Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct = default) =>
        creditorBindings.EnsureCreditorContactAsync(userId, legalName, burnerName, iban,
            seedContactId, seedAccountNum, jobName, ct);

    public Task SetCreditorAccountNumAsync(
        Guid userId, int supplierAccountNum, string jobName, CancellationToken ct = default) =>
        creditorBindings.SetCreditorAccountNumAsync(userId, supplierAccountNum, jobName, ct);

    public Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default) =>
        creditorBindings.ClearCreditorContactAsync(userId, actorUserId, ct);

    // Persisted in audit_log.entity_type; pinned so a rename cannot silently orphan old rows.
    private const string SepaTransferEntityType = nameof(SepaPayoutTransfer);

    public SepaPayoutSettings GetSepaPayoutSettings() => sepa.Value.GetPayoutSettings();

    public async Task<SepaPayoutPreview> PreviewSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, CancellationToken ct = default)
    {
        var (plan, error) = await PlanSepaPayoutAsync(selections, maxPerTransfer, ct);
        return plan is null ? SepaPayoutPreview.Failure(error!) : new SepaPayoutPreview(plan.Lines, null);
    }

    public async Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default)
    {
        var (plan, error) = await PlanSepaPayoutAsync(selections, maxPerTransfer, ct);
        if (plan is null) return SepaPayoutResult.Failure(error!);
        var (fileId, now, transfers, _, xml) = plan;

        // The stamp is minute-resolution, and the name is what ties a file on the treasurer's disk to its row, so the
        // file id's first 8 hex characters keep two batches in one minute apart.
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

        // After the save: a rolled-back write must not leave a ghost audit row.
        foreach (var t in transfers)
            await audit.LogAsync(
                FinanceAuditActions.SepaPayoutTransfer, SepaTransferEntityType, t.Id,
                $"SEPA payout {Euros(t.Amount)} to {t.IbanMasked} on creditor account "
                + $"{t.SupplierAccountNum} (file {fileName}).",
                actorUserId, t.UserId, AuditSubjectTypes.User);

        // The payout is committed: abandoning the download must not suppress its notifications.
        await payoutEmails.SendPayoutEmailsAsync(transfers);

        return new SepaPayoutResult(fileName, xml, null);
    }

    private sealed record SepaPayoutPlan(
        Guid FileId, Instant Now, IReadOnlyList<SepaPayoutTransfer> Transfers,
        IReadOnlyList<SepaPayoutPreviewLine> Lines, string Xml);

    /// <summary>Everything but the write, so the confirmation page refuses exactly what generation would.</summary>
    private async Task<(SepaPayoutPlan? Plan, string? Error)> PlanSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, CancellationToken ct)
    {
        var settings = GetSepaPayoutSettings();
        if (settings.UnavailableReason is { } unavailable)
            return (null, unavailable);

        if (selections.Count == 0)
            return (null, "No creditor account was selected — nothing to generate.");

        if (selections.GroupBy(s => s.SupplierAccountNum).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            return (null,
                $"Creditor account {duplicate.Key} was selected twice — a payout pays each account once.");

        // The file pays the IBAN on Holded's contact now, not last night's: a fix made in Holded today must not pay the
        // old account. So a payout refreshes the contact mirror first and refuses without it.
        try
        {
            if (!await holded.SyncContactsAsync(ct))
                return (null, "A Holded contact sync is already running, so the payees' IBANs cannot be "
                              + "confirmed — try again in a moment.");
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            return (null,
                $"Holded's contacts could not be refreshed, so the payees' IBANs cannot be confirmed: {ex.Message}");
        }

        var (rows, _, contactsUnavailable) = await ListCreditorAccountsAsync(ct);
        if (contactsUnavailable is not null)
            return (null, contactsUnavailable);
        var byAccount = rows.ToDictionary(r => r.SupplierAccountNum);

        // The unmasked IBAN goes nowhere but the file and the payout record. Keyed by contact id, not account number:
        // two contacts can share one 400000xx, and only the bound one is the member's.
        if (await CreditorReader.ContactsByIdAsync(ct) is not { } contactById)
            return (null, ContactListUnavailable);

        var fileId = Guid.NewGuid();
        var transfers = new List<SepaPayoutTransfer>(selections.Count);
        var lines = new List<SepaPayoutPreviewLine>(selections.Count);

        foreach (var s in selections)
        {
            if (!byAccount.TryGetValue(s.SupplierAccountNum, out var row))
                return (null,
                    $"Creditor account {s.SupplierAccountNum} is no longer listed — reload the page and try again.");

            if (row.NotPayableReason is { } notPayable)
                return (null, notPayable);

            if (s.Amount > row.OwedToMember)
                return (null,
                    $"Creditor account {s.SupplierAccountNum}: {Euros(s.Amount)} is more than the "
                    + $"{Euros(row.OwedToMember)} owed.");

            // Never pay a contact or IBAN the mirror lacks.
            if (!contactById.TryGetValue(row.Bindings[0].HoldedContactId, out var contact)
                || string.IsNullOrWhiteSpace(contact.Iban))
                return (null,
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
            lines.Add(new SepaPayoutPreviewLine(
                s.SupplierAccountNum, row.Bindings[0].UserId, transfers[^1].CreditorName, transfers[^1].IbanMasked,
                row.OwedToMember, s.Amount));
        }

        var now = clock.GetCurrentInstant();
        var request = new SepaPaymentFileRequest(
            MsgId: "M" + fileId.ToString("N"),
            CreatedAt: now,
            RequestedExecutionDate: now.InZone(MadridZone).Date,
            Debtor: new SepaDebtor(
                sepa.Value.CreditorName!, sepa.Value.CreditorIban!,
                sepa.Value.CreditorBic, sepa.Value.CreditorIdentifier!),
            MaxAmountPerTransfer: maxPerTransfer,
            Transfers: transfers
                .Select(t => new SepaTransfer(t.CreditorName, t.Iban, t.Amount, t.SupplierAccountNum))
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
            return (null, ex.Message);
        }

        return (new SepaPayoutPlan(fileId, now, transfers, lines, xml), null);
    }

    public Task<SepaBookingResult> WithdrawSepaTransferAsync(
        Guid transferId, Guid actorUserId, CancellationToken ct = default) =>
        booking.WithdrawSepaTransferAsync(transferId, actorUserId, payoutEmails, ct);

    public Task<SepaBookingResult> CancelSepaFileAsync(Guid fileId, Guid actorUserId, CancellationToken ct = default) =>
        booking.CancelSepaFileAsync(fileId, actorUserId, ct);

    public Task<(IReadOnlyList<SepaPayoutTransferRow> Transfers, string? UnavailableReason)>
        GetSepaTransfersAsync(CancellationToken ct = default) => booking.GetSepaTransfersAsync(ct);

    public Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError, Instant? BankFeedSyncedAt)>
        GetSepaPayoutsAsync(int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays,
            CancellationToken ct = default) => booking.GetSepaPayoutsAsync(toleranceDays, ct);

    public Task RunSepaBookingSweepAsync(CancellationToken ct = default) => booking.RunAsync(ct);

    public Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        booking.BookSepaTransferAsync(transferId, bankMovementId, actorUserId, toleranceDays);

    public Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        booking.BookSepaFileAsync(fileId, bankMovementId, actorUserId, toleranceDays);

    public Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        booking.BookSepaFileMatchesAsync(fileId, actorUserId, toleranceDays);

    /// <summary>Safe in a download filename.</summary>
    private static string FileSlug(string name)
    {
        var cleaned = new string(SepaText.Normalize(name, 32).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray());
        var slug = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "sepa" : slug;
    }

    /// <summary>The kept expense line: its id is the one Guid in the story; Holded ids go in the text.</summary>
    private const string ExpenseLineEntityType = "ExpenseLine";

    public Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default) =>
        BuildDuplicatePlanAsync(ct);

    /// <summary>
    /// From the mirrors, never a stored plan. The claim is re-run on each candidate's mirrored notes, so a candidate
    /// goes to the line whose notes it carries and only an exact match can be deleted. One whose notes or payments the
    /// mirror does not know yet is refused.
    /// </summary>
    private async Task<HoldedDuplicatesVm> BuildDuplicatePlanAsync(CancellationToken ct)
    {
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        if (mirror.LastSyncAt is null)
            return new HoldedDuplicatesVm([], [], PurchaseMirrorUnavailable);
        var docs = mirror.Docs.Select(ToListItem).ToList();
        var notesById = mirror.Docs.ToDictionary(d => d.Id, d => d.Notes, StringComparer.Ordinal);
        var paymentsUnknown = mirror.Docs.Where(d => d.PaymentsPending is null).Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        string? Refusal(HoldedPurchaseDocListItemDto d) => paymentsUnknown.Contains(d.Id)
            ? "its payments are not mirrored yet — sync Holded and look again"
            : DeleteRefusal(d);
        // Without it the same-named sibling contacts go unseen, so the plan would look complete while missing them.
        if (await CreditorReader.ContactsByIdAsync(ct) is not { } contactById)
            return new HoldedDuplicatesVm([], [], ContactListUnavailable);
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var bookings = await expenses.GetHoldedBookingsAsync(ct);
        var contactsByUser = MemberContacts(bookings, bindings, contactById);
        var bindingByUser = bindings.ToDictionary(b => b.UserId);
        var docsByContact = docs.Where(d => !string.IsNullOrEmpty(d.ContactId))
            .ToLookup(d => d.ContactId!, StringComparer.Ordinal);

        var candidates = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser);
        var verified = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser,
            (b, d) => HoldedDuplicateMatcher.Matches(b, d)
                      && Refusal(d) is null
                      && notesById.GetValueOrDefault(d.Id) is { } notes
                      && string.Equals(notes, b.Notes, StringComparison.Ordinal));
        var deletableIds = verified.SelectMany(m => m.Duplicates.Select(d => d.Id)).ToHashSet(StringComparer.Ordinal);
        var byRef = (IEqualityComparer<ExpenseHoldedBooking>)ReferenceEqualityComparer.Instance;
        var candidateByBooking = candidates.ToDictionary(m => m.Booking, byRef);
        var verifiedByBooking = verified.ToDictionary(m => m.Booking, byRef);

        var matched = new List<(ExpenseHoldedBooking Booking, HoldedPurchaseDocListItemDto Kept, HoldedDuplicateLine Line)>();
        foreach (var linked in bookings)
        {
            var named = candidateByBooking.GetValueOrDefault(linked);
            var ok = verifiedByBooking.GetValueOrDefault(linked);
            if (named is null && ok is null) continue;
            var deletable = ok?.Duplicates.Select(d => d.Id).ToList() ?? [];
            var refused = (named?.Duplicates ?? [])
                .Where(d => !deletableIds.Contains(d.Id))
                .Select(d => new HoldedDuplicateRefusal(d.Id, d.Total, Refusal(d)
                    ?? (notesById.GetValueOrDefault(d.Id) is null
                        ? "its notes are not mirrored yet — the next sync reads them"
                        : "notes do not match the push — judge it by hand")))
                .ToList();
            if (deletable.Count == 0 && refused.Count == 0) continue;
            var kept = (ok ?? named)!.Kept;
            matched.Add((linked, kept, new HoldedDuplicateLine(
                linked.ExpenseReportId, linked.LineId, linked.Description,
                linked.BookedAmount, kept.Id, deletable, refused)));
        }

        var members = matched
            .GroupBy(l => l.Booking.CreditorUserId)
            .Select(g =>
            {
                var binding = bindingByUser.GetValueOrDefault(g.Key);
                var lines = g.Select(l => l.Line).ToList();

                // A contact the race created beside the bound one holds only duplicates, so it goes once they do.
                var deletableIds = lines.SelectMany(l => l.DeletableDocIds).ToHashSet(StringComparer.Ordinal);
                var orphans = contactsByUser[g.Key]
                    .Where(c => !string.Equals(c, binding?.HoldedContactId, StringComparison.Ordinal))
                    .Where(c => docsByContact[c].Any() && docsByContact[c].All(d => deletableIds.Contains(d.Id)))
                    .Order(StringComparer.Ordinal)
                    .ToList();

                var contact = (binding?.HoldedContactId is { Length: > 0 } boundId ? contactById.GetValueOrDefault(boundId) : null)
                              ?? contactById.GetValueOrDefault(g.First().Kept.ContactId ?? "");
                return new HoldedDuplicateMember(
                    g.Key,
                    contact?.Name ?? g.First().Kept.ContactName,
                    binding?.HoldedContactId,
                    contact?.SupplierAccountNum,
                    lines, orphans);
            })
            .ToList();

        // Holded has no archive call, so an empty unbound contact is listed, not touched.
        var boundContacts = bindings.Select(b => b.HoldedContactId).ToHashSet(StringComparer.Ordinal);
        var empty = contactById.Values
            .Where(c => IsMemberCreditorAccount(c.SupplierAccountNum)
                        && !boundContacts.Contains(c.Id) && !docsByContact[c.Id].Any())
            .Select(c => (AccountNum: c.SupplierAccountNum!.Value, Summary: $"{c.SupplierAccountNum} {c.Name} ({c.Id})"))
            .ToList();

        return new HoldedDuplicatesVm(members, empty, null);
    }

    /// <summary>Holded refuses to delete a document carrying a payment (a reconciled bank line counts), and a draft
    /// reports no <c>payments_pending</c>, so it reads as paid too.</summary>
    private static string? DeleteRefusal(HoldedPurchaseDocListItemDto d)
    {
        if (d.PaymentsPending >= d.Total) return null;
        return d.IsDraft == true
            ? "still a draft in Holded — delete it by hand"
            : $"has a payment on it ({Euros(d.Total - d.PaymentsPending)} of {Euros(d.Total)} paid) — settle it in Holded by hand";
    }

    public async Task<HoldedDuplicateApplyResult> RemoveHoldedDuplicatesAsync(Guid actorUserId)
    {
        // A deletion that has started finishes.
        var ct = CancellationToken.None;

        HoldedDuplicatesVm plan;
        try
        {
            // Deletion judges Holded as it is now, not as the nightly sync left it.
            if (!await holded.SyncPurchaseDocumentsAsync(ct) || !await holded.SyncContactsAsync(ct))
                return new HoldedDuplicateApplyResult(
                    [], "A Holded sync is already running — nothing was deleted; try again in a moment.", null);
            plan = await BuildDuplicatePlanAsync(ct);
            // The list sync keeps notes it already holds, and an accountant may have edited them since: re-read the
            // notes of every document about to be deleted, then judge again.
            var deletable = plan.Members.SelectMany(m => m.Lines).SelectMany(l => l.DeletableDocIds).ToList();
            if (deletable.Count > 0)
            {
                await holded.SyncPurchaseDocumentNotesAsync(deletable, ct);
                plan = await BuildDuplicatePlanAsync(ct);
            }
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("Holded could not be read; no duplicate was deleted. {Reason}", ex.Message);
            return new HoldedDuplicateApplyResult([], "Holded could not be read — nothing was deleted: " + ex.Message, null);
        }
        if (plan.Error is { } planError)
            return new HoldedDuplicateApplyResult([], "Nothing was deleted: " + planError, null);
        if (!plan.HasWork)
            return new HoldedDuplicateApplyResult([], "No duplicate document was found — nothing was deleted.", null);

        var before = await ReadCreditorBalancesAsync(ct);
        var results = new List<HoldedDuplicateMemberResult>();
        foreach (var member in plan.Members.OrderBy(m => m.SupplierAccountNum ?? int.MaxValue)
                     .ThenBy(m => m.ContactName, StringComparer.OrdinalIgnoreCase))
        {
            var account = member.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            var deletedDocs = new List<string>();
            var deletedContacts = new List<string>();
            var failures = new List<string>();
            foreach (var line in member.Lines)
                foreach (var docId in line.DeletableDocIds)
                {
                    try
                    {
                        await holdedWrite.DeletePurchaseDocumentByIdAsync(docId, ct);
                        deletedDocs.Add(docId);
                        await audit.LogAsync(
                            FinanceAuditActions.HoldedDuplicateRemoved, ExpenseLineEntityType, line.LineId,
                            $"DELETED duplicate Holded purchase document {docId} ({Euros(line.Amount)}, "
                            + $"'{line.Description}') of {member.ContactName} on creditor account {account}; "
                            + $"kept {line.KeptDocId}.",
                            actorUserId, member.UserId, AuditSubjectTypes.User);
                    }
                    catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                    {
                        logger.LogError(ex, "Holded refused to delete duplicate purchase document {DocId}.", docId);
                        failures.Add($"document {docId}: {ex.Message}");
                    }
                }

            // A refused document keeps its contact.
            if (failures.Count == 0)
                foreach (var contactId in member.OrphanContactIds)
                {
                    try
                    {
                        await holdedWrite.DeleteContactAsync(contactId, ct);
                        deletedContacts.Add(contactId);
                        await audit.LogAsync(
                            FinanceAuditActions.HoldedDuplicateRemoved, AuditSubjectTypes.User, member.UserId,
                            $"DELETED duplicate Holded contact {contactId} of {member.ContactName}; kept "
                            + $"{member.BoundContactId ?? "the bound contact"}.",
                            actorUserId);
                    }
                    catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                    {
                        logger.LogError(ex, "Holded refused to delete duplicate contact {ContactId}.", contactId);
                        failures.Add($"contact {contactId}: {ex.Message}");
                    }
                }

            results.Add(new HoldedDuplicateMemberResult(
                member.UserId, member.ContactName, member.SupplierAccountNum,
                member.SupplierAccountNum is { } num ? before?.GetValueOrDefault(num) : null, null,
                deletedDocs, deletedContacts, failures));
        }

        string? syncError = null;
        try
        {
            await holded.SyncPurchaseDocumentsAsync(ct);
            await holded.SyncLedgerAsync(ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The Holded sync after removing duplicates failed; the deletions stand.");
            syncError = ex.Message;
        }

        var after = await ReadCreditorBalancesAsync(ct);
        return new HoldedDuplicateApplyResult(
            results.Select(r => r with
            {
                BalanceAfter = r.SupplierAccountNum is { } num ? after?.GetValueOrDefault(num) : null,
            }).ToList(),
            null, syncError);
    }

    /// <summary>Holded's own sign. Null when unreadable, so the result shows no figure rather than a stale one.</summary>
    private async Task<Dictionary<int, decimal>?> ReadCreditorBalancesAsync(CancellationToken ct)
    {
        try
        {
            await holded.SyncChartAsync(ct);
            return (await holded.GetChartAsync(ct))
                .Where(a => IsMemberCreditorAccount(a.Number))
                .GroupBy(a => a.Number)
                .ToDictionary(g => g.Key, g => g.First().HoldedBalance);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("Holded's chart of accounts could not be read for the duplicate removal balances. {Reason}", ex.Message);
            return null;
        }
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var bindings = new List<HoldedCreditorContact>();
        var payouts = new List<SepaPayoutExportRow>();
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            if (await repo.GetCreditorContactByUserAsync(id, ct) is { } binding)
                bindings.Add(binding);
            payouts.AddRange(await repo.GetSepaPayoutsForUserAsync(id, ct));
        }
        // Free-text descriptions included: they are the member's data too.
        var ledgerLines = new List<CreditorLedgerLine>();
        var boundContacts = new List<HoldedContactDto>();
        if (bindings.Count > 0)
        {
            // Without the contact list no binding resolves to an account, and an export missing the member's ledger
            // is wrong, not partial: stop, and let the retry pick it up.
            var contacts = await CreditorReader.ContactsByIdAsync(ct)
                ?? throw new InvalidOperationException(
                    $"GDPR export for member {userId} stopped: {ContactListUnavailable}");
            // A contact with no creditor number yet has no ledger to export; a contact Holded's list lacks might.
            if (bindings.FirstOrDefault(b => !contacts.ContainsKey(b.HoldedContactId)) is { } missing)
                throw new InvalidOperationException(
                    $"GDPR export for member {userId} stopped: their Holded contact {missing.HoldedContactId} is "
                    + "missing from Holded's contact list, so their ledger cannot be found. Rebind or unbind them on "
                    + "/Finance/Creditors.");

            // Ledger lines carry no user id, so an account another member is also bound to would leak their lines.
            // Withheld until an admin resolves the collision.
            var ownIds = bindings.Select(b => b.UserId).ToHashSet();
            var othersBindings = (await repo.GetCreditorContactsAsync(ct))
                .Where(b => !ownIds.Contains(b.UserId))
                .ToList();
            var shared = othersBindings
                .Select(b => ResolveCreditorAccount(b, contacts))
                .OfType<int>()
                .ToHashSet();
            // A contact another member is bound to may carry their details, so it waits for the collision too.
            var othersContacts = othersBindings.Select(b => b.HoldedContactId).ToHashSet(StringComparer.Ordinal);
            boundContacts.AddRange(bindings.Select(b => b.HoldedContactId)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !othersContacts.Contains(id))
                .Select(id => contacts[id]));
            foreach (var num in bindings.Select(b => ResolveCreditorAccount(b, contacts)).OfType<int>().Distinct())
            {
                if (shared.Contains(num))
                {
                    logger.LogWarning(
                        "GDPR export for member {UserId} withheld the creditor ledger of account {AccountNum}: " +
                        "another member is bound to it too. Resolve the collision on /Finance/Creditors.",
                        userId, num);
                    continue;
                }
                if (await GetCreditorLedgerAsync(num, ct) is { } ledger)
                    ledgerLines.AddRange(ledger.Lines);
            }
        }
        var currentBinding = bindings.FirstOrDefault(binding => binding.UserId == userId);
        var additionalBindings = bindings.Where(binding => binding.UserId != userId).ToList();
        return
        [
            new UserDataSlice(HoldedCreditorAccount, currentBinding is null ? null : new
            {
                currentBinding.SupplierAccountNum,
                currentBinding.HoldedContactId,
                Source = currentBinding.Source.ToString(),
            }),
            new UserDataSlice(AdditionalHoldedCreditorAccounts,
                additionalBindings.Count == 0 ? null : additionalBindings.Select(binding => new
                {
                    binding.UserId,
                    binding.SupplierAccountNum,
                    binding.HoldedContactId,
                    Source = binding.Source.ToString(),
                }).ToList()),

            // Masked IBAN, as everywhere outside the file and the payout row.
            new UserDataSlice(SepaPayouts, payouts.OrderBy(p => p.GeneratedAt).Select(p => new
            {
                GeneratedAt = p.GeneratedAt.ToIso8601(),
                p.FileName,
                p.SupplierAccountNum,
                p.HoldedContactId,
                p.CreditorName,
                Iban = p.IbanMasked,
                p.Amount,
                BookedAt = p.BookedAt?.ToIso8601(),
                p.HoldedBankMovementId,
                ReconciledAt = p.ReconciledAt?.ToIso8601(),
                WithdrawnAt = p.WithdrawnAt?.ToIso8601(),
            })),

            new UserDataSlice(CreditorLedger, ledgerLines.Count == 0 ? null : ledgerLines.Select(l => new
            {
                l.AccountNum,
                Date = l.Date.ToIso8601(),
                l.EntryNumber,
                l.Line,
                l.Type,
                l.Description,
                l.Debit,
                l.Credit,
                l.RunningBalance,
            }).ToList()),

            // The Holded contact mirror's row for each bound contact, IBAN masked as above.
            new UserDataSlice(HoldedCreditorContacts, boundContacts.Count == 0 ? null : boundContacts.Select(c => new
            {
                c.Id,
                c.Name,
                c.TradeName,
                c.SupplierAccountNum,
                c.Email,
                c.Phone,
                c.Mobile,
                Iban = string.IsNullOrWhiteSpace(c.Iban) ? null : IbanFormatter.Mask(c.Iban),
                c.TaxCode,
                c.Address,
            }).ToList()),
        ];
    }

    private const string PayoutRetention =
        "Retained in full, nothing erased: a payout file is the credit-transfer order the bank was " +
        "given, and it keeps the payee's legal name, their IBAN (stored unmasked in both the transfer " +
        "row and the file's XML — the export masks it, these do not), the creditor account and the " +
        "amount. Spanish law requires the books and their supporting documents be kept 6 years " +
        "(Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66), and a " +
        "payment order stripped of its payee is no longer evidence of the payment. GDPR Art. 17(3)(b).";

    private const string LedgerRetention =
        "Retained in full, nothing erased: the creditor ledger lines are Holded's journal entries for " +
        "the member's creditor account (amounts, dates and their free-text descriptions), mirrored here " +
        "from the books. Spanish law requires the books and their supporting documents be kept 6 years " +
        "(Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66). The lines carry " +
        "no user id: once the creditor binding is erased they are no longer linkable to the person " +
        "through this system. GDPR Art. 17(3)(b).";

    private const string ContactRetention =
        "Retained, nothing erased here: these rows mirror the member's creditor contact in Holded, the " +
        "association's accounting system, where it is the counterparty record of the books. Spanish law " +
        "requires the books and their supporting documents be kept 6 years (Código de Comercio Art. 30) " +
        "and 4 years for tax purposes (Ley 58/2003 Art. 66). The mirror is replaced from Holded on every " +
        "sync, so it holds what Holded holds and no more. GDPR Art. 17(3)(b).";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HoldedCreditorAccount] = null,
            [AdditionalHoldedCreditorAccounts] = null,
            [SepaPayouts] = PayoutRetention,
            [CreditorLedger] = LedgerRetention,
            [HoldedCreditorContacts] = ContactRetention,
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The invoices live in Holded as fiscal records; the payout files survive on the same basis (see
    /// <see cref="PayoutRetention"/>).
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        creditorBindings.DeleteCreditorBindingAsync(userId, ct);


}
