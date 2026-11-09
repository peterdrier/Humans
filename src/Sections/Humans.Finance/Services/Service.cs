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

    internal const string PurchaseMirrorUnavailable = HoldedDuplicateReview.PurchaseMirrorUnavailable;

    internal const string ContactListUnavailable = HoldedCreditorReader.ContactListUnavailable;
    private SepaPayoutGeneration PayoutGeneration => new(repo, holded, CreditorReader, clock, audit, sepa, payoutEmails, logger);
    private HoldedDuplicateReview DuplicateReview => new(repo, holded, holdedWrite, expenses, CreditorReader, audit, logger);
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

    public SepaPayoutSettings GetSepaPayoutSettings() => PayoutGeneration.GetSepaPayoutSettings();

    public Task<SepaPayoutPreview> PreviewSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, CancellationToken ct = default) =>
        PayoutGeneration.PreviewSepaPayoutAsync(selections, maxPerTransfer, ct);

    public Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default) =>
        PayoutGeneration.GenerateSepaPayoutAsync(selections, maxPerTransfer, actorUserId, ct);

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

    public Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default) =>
        DuplicateReview.GetHoldedDuplicatesAsync(ct);

    public Task<HoldedDuplicateApplyResult> RemoveHoldedDuplicatesAsync(Guid actorUserId) =>
        DuplicateReview.RemoveHoldedDuplicatesAsync(actorUserId);

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
