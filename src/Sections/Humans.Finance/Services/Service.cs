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

    internal const string HoldedCreditorAccount = FinancePersonalData.HoldedCreditorAccount;
    internal const string SepaPayouts = FinancePersonalData.SepaPayouts;
    internal const string CreditorLedger = FinancePersonalData.CreditorLedger;
    internal const string HoldedCreditorContacts = FinancePersonalData.HoldedCreditorContacts;

    internal const string HoldedExpenseAccount = HoldedCategoryAccounts.HoldedExpenseAccount;

    internal const string PurchaseMirrorUnavailable = HoldedDuplicateReview.PurchaseMirrorUnavailable;

    internal const string ContactListUnavailable = HoldedCreditorReader.ContactListUnavailable;
    private FinancePersonalData PersonalData => new(repo, users, CreditorReader, creditorBindings, logger);
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

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.ContributeForUserAsync(userId, ct);

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => PersonalData.ErasureDeclaration;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.EraseForUserAsync(userId, ct);

}
