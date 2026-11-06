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

    internal const string HoldedCreditorAccount = "HoldedCreditorAccount";
    internal const string AdditionalHoldedCreditorAccounts = "AdditionalHoldedCreditorAccounts";
    internal const string SepaPayouts = "SepaPayouts";
    internal const string CreditorLedger = "CreditorLedger";
    internal const string HoldedCreditorContacts = "HoldedCreditorContacts";

    /// <summary>First number of the P&amp;L expense block both provisioning paths allocate from.</summary>
    internal const int ExpenseAccountBlockStart = 62900100;

    // PGC group 6, the accounts Holded's expenses-accounts list holds.
    private const int ExpenseAccountsFrom = 60000000;
    private const int ExpenseAccountsTo = 69999999;
    internal const string HoldedExpenseAccount = HoldedCategoryAccounts.HoldedExpenseAccount;

    internal const string PurchaseMirrorUnavailable =
        "The Holded purchase-document mirror has never synced, so no duplicate can be judged. Run Sync now on /Holded.";

    internal const string ContactListUnavailable =
        "The Holded contact mirror has never synced, so no member's creditor account is known. Run Sync now on "
        + "/Holded.";
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private readonly HoldedCategoryAccounts categoryAccounts = new(repo, holded, holdedWrite, budget, clock, audit, logger);
    private readonly SepaBankBooking booking = new(repo, holded, holdedWrite, clock, bookingState, audit, sepa, logger);
    private readonly SepaPayoutEmails payoutEmails = new(users, emailService, emails, logger);

    public Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default) =>
        categoryAccounts.GetProvisioningPlanAsync(blockStart, ct);

    public Task<int> ProvisionAsync(int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default) =>
        categoryAccounts.ProvisionAsync(blockStart, addAll, actorUserId, ct);

    public async Task<ExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        IReadOnlyList<HoldedChartAccountInfo> chart;
        try
        {
            // Matching by name against a stale mirror would miss an account made in Holded's UI and mint its twin.
            await holded.SyncChartAsync(ct);
            chart = await holded.GetChartAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh the Holded chart while creating or linking account '{Name}'", name);
            throw;
        }

        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var expenseChart = chart.Where(a => a.Number is >= ExpenseAccountsFrom and <= ExpenseAccountsTo).ToList();

        HoldedChartAccountInfo? found;
        if (existingAccountNum is { } num)
        {
            found = expenseChart.FirstOrDefault(a => a.Number == num)
                ?? throw new InvalidOperationException($"Holded has no expense account {num}.");
        }
        else
        {
            var wanted = HoldedMatcher.NormalizeAccountName(name);
            found = expenseChart.FirstOrDefault(a =>
                !a.Archived
                && string.Equals(HoldedMatcher.NormalizeAccountName(a.Name), wanted, StringComparison.Ordinal));
        }

        var now = clock.GetCurrentInstant();
        if (found is not null)
        {
            // In the mirror, so this only reads its id.
            var linked = await holdedWrite.EnsureExpenseAccountAsync(found.Number, found.Name, ct);
            var isCategoryAccount = map.Any(m => m.HoldedAccountNumber == found.Number);
            if (!isCategoryAccount)
                await RegisterManagedAsync(found.Number, linked.HoldedId, found.Name, managed, now, ct);
            await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountLinked, HoldedExpenseAccount, Guid.Empty,
                $"Linked Holded expense account {found.Number} '{found.Name}' for '{name}'", "Finance");
            return new ExpenseAccountRef(found.Number, linked.HoldedId, found.Name, Created: false);
        }

        var used = map.Select(m => m.HoldedAccountNumber)
            .Concat(managed.Select(m => m.HoldedAccountNumber))
            .Concat(chart.Select(a => a.Number))
            .ToHashSet();
        var next = ExpenseAccountBlockStart;
        while (used.Contains(next)) next++;

        HoldedExpenseAccountOutcome created;
        try
        {
            created = await holdedWrite.EnsureExpenseAccountAsync(next, name, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Holded expense account {AccountNum} '{Name}'", next, name);
            throw;
        }

        await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountCreated, HoldedExpenseAccount, Guid.Empty,
            $"Created Holded expense account {next} '{name}'", "Finance");
        await RegisterManagedAsync(next, created.HoldedId, name, managed, now, ct);
        return new ExpenseAccountRef(next, created.HoldedId, name, Created: true);
    }

    private Task RegisterManagedAsync(
        int accountNum, string accountId, string label,
        IReadOnlyList<HoldedManagedAccount> managed, Instant now, CancellationToken ct)
    {
        var existing = managed.FirstOrDefault(m => m.HoldedAccountNumber == accountNum);
        return repo.UpsertManagedAccountAsync(new HoldedManagedAccount
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            HoldedAccountNumber = accountNum,
            HoldedAccountId = accountId,
            Label = label,
            IsActive = true,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        }, ct);
    }

    public async Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default)
    {
        var managed = await repo.GetManagedAccountsAsync(ct);
        var row = managed.FirstOrDefault(m => m.HoldedAccountNumber == accountNum);
        if (row is null || row.IsActive == isActive) return;

        row.IsActive = isActive;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpsertManagedAccountAsync(row, ct);
        await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountActiveChanged, HoldedExpenseAccount, Guid.Empty,
            $"{(isActive ? "Restored" : "Retired")} Holded expense account {accountNum} '{row.Label}'", "Finance");
    }

    public async Task<IReadOnlyList<ExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var labels = (await categoryAccounts.ActiveYearCategoriesAsync(ct))
            .ToDictionary(c => c.Key, c => $"{c.Value.Group} / {c.Value.Name}");

        // A map row whose category is not in the active year still labels old reports but takes no new booking, so it
        // is inactive here and dropped with activeOnly.
        var options = map
            .Select(m => new ExpenseAccountOption(
                m.HoldedAccountNumber, m.HoldedAccountId,
                labels.GetValueOrDefault(m.BudgetCategoryId, $"Category {m.HoldedAccountNumber}"),
                IsBudgetCategory: true, IsActive: labels.ContainsKey(m.BudgetCategoryId)))
            .Where(o => !activeOnly || o.IsActive)
            .Concat(managed
                .Where(m => !activeOnly || m.IsActive)
                .Select(m => new ExpenseAccountOption(
                    m.HoldedAccountNumber, m.HoldedAccountId, m.Label,
                    IsBudgetCategory: false, IsActive: m.IsActive)))
            .ToList();
        return options;
    }

    /// <summary>The purchase-document mirror with each document's category attribution: account match over tag
    /// match, a managed account matches with no category. Computed on read, so a mapping fixed in Holded or added
    /// here applies on the next page load.</summary>
    private async Task<(HoldedPurchaseDocMirror Mirror, IReadOnlyList<AttributedDoc> Docs)> AttributedDocsAsync(
        CancellationToken ct)
    {
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        var entries = (await repo.GetCategoryMapAsync(ct))
            .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.Tag))
            .ToArray();
        var managedIds = (await repo.GetManagedAccountsAsync(ct))
            .Select(m => m.HoldedAccountId)
            .ToHashSet(StringComparer.Ordinal);
        return (mirror, mirror.Docs
            .Select(d => new AttributedDoc(d, HoldedMatcher.Match(d.BookedAccountId, d.Tags, entries, managedIds)))
            .ToList());
    }

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

        if (await ContactsByIdAsync(ct) is not { } contacts)
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

    public async Task<IReadOnlyList<CategoryActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default)
    {
        // Doc-derived, not ledger balances: the budget pages are gross/IVA-inclusive while a 629
        // balance is net, and the ledger carries drafts Holded has not approved.
        var (_, docs) = await AttributedDocsAsync(ct);
        return docs
            .Where(d => d.Doc.IsApproved && d.Doc.Date.Year == calendarYear && d.Match.CategoryId is not null)
            .GroupBy(d => d.Match.CategoryId!.Value)
            .Select(g => new CategoryActualRow(
                g.Key,
                g.Sum(d => d.Doc.Total),
                g.Select(d => new CategoryActualDoc(
                        d.Doc.Id, d.Doc.DocNumber, d.Doc.ContactName, d.Doc.Description, d.Doc.Date, d.Doc.Total,
                        d.Doc.HoldedUrl))
                    .ToList()))
            .Where(r => r.Actual != 0m)
            .ToList();
    }

    public async Task<IReadOnlyList<UnmatchedDocRow>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var (_, docs) = await AttributedDocsAsync(ct);
        return docs
            .Where(d => d.IsUnmatched)
            .Select(d => new UnmatchedDocRow(
                d.Doc.Id,
                d.Doc.DocNumber,
                d.Doc.ContactName,
                d.Doc.Description,
                d.Doc.Total,
                ReasonFor(d.Doc),
                d.Doc.HoldedUrl)
            { Date = d.Doc.Date })
            .ToList();
    }

    public Task<string?> GetHoldedAccountIdForCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default) =>
        categoryAccounts.GetHoldedAccountIdForCategoryAsync(budgetCategoryId, ct);

    public Task<IReadOnlyList<CategoryAccountRow>> GetCategoryMapAsync(CancellationToken ct = default) =>
        categoryAccounts.GetCategoryMapAsync(ct);

    public async Task<IReadOnlyList<HoldedManagedAccountVm>> GetManagedAccountsAsync(CancellationToken ct = default) =>
        (await repo.GetManagedAccountsAsync(ct))
            .Select(m => new HoldedManagedAccountVm(m.HoldedAccountNumber, m.HoldedAccountId, m.Label, m.IsActive, m.CreatedAt))
            .ToList();

    private static string ReasonFor(HoldedPurchaseDocInfo d)
    {
        var hasAccount = !string.IsNullOrEmpty(d.BookedAccountId);
        var hasTags = d.Tags.Count > 0;

        if (!hasAccount && !hasTags)
            return "No account, no tag";
        if (hasAccount && hasTags)
            return "Account and tags not mapped";
        if (hasAccount)
            return "Account not mapped";
        return "Tags not matched";
    }

    // A new expense-only contact gets type "creditor", which Holded numbers in the 410 series rather than the
    // 400-series proveedor accounts older members carry, so the block spans both. Bindings, not the range, tell a
    // member's account from an org vendor's.
    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 41999999;

    private static bool IsMemberCreditorAccount(int? accountNum) =>
        accountNum is >= CreditorAccountMin and <= CreditorAccountMax;

    public async Task<CreditorStatus?> GetCreditorStatusAsync(
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

    public async Task<(IReadOnlyList<CreditorAccountRow> Accounts,
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
    private static int? ResolveCreditorAccount(
        HoldedCreditorContact binding, IReadOnlyDictionary<string, HoldedContactDto> contacts)
    {
        var account = contacts.GetValueOrDefault(binding.HoldedContactId)?.SupplierAccountNum;
        return IsMemberCreditorAccount(account) ? account : null;
    }

    /// <summary>Keyed by contact id, the identity a binding stores. The rows and the payout both resolve through this
    /// one map, so a row's name and IBAN cannot disagree with the file's. From the contact mirror; null when it has
    /// never synced.</summary>
    private async Task<Dictionary<string, HoldedContactDto>?> ContactsByIdAsync(CancellationToken ct) =>
        await holded.GetContactsAsync(ct) is not { LastSyncAt: not null } mirror
            ? null
            : mirror.Contacts
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public async Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var b = await repo.GetCreditorContactByUserAsync(userId, ct);
        return b is null ? null : ToBinding(b);
    }

    private static CreditorContactBinding ToBinding(HoldedCreditorContact b) =>
        new(b.UserId, b.HoldedContactId, b.SupplierAccountNum, b.Source) { CreatedAt = b.CreatedAt };

    // An account, and the Holded contact behind it, binds to at most one member. All three write paths check with
    // FindConflictingBinding and differ only in the remedy: refuse when the value is our guess, write and let the
    // collision show when it is Holded's own word. Not a unique DB index; see Docs/health.md.

    /// <summary>Overlap on either the 400000xx or the contact merges two members' payables, so both are conflicts.</summary>
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

    /// <summary>The admin sees the collision as a duplicate row on /Finance/Creditors; this is the trail of how it
    /// arrived.</summary>
    private void LogBindingCollision(
        string writePath, Guid userId, string holdedContactId, int? supplierAccountNum,
        HoldedCreditorContact conflict) =>
        logger.LogError(
            "Creditor binding collision in {WritePath}: member {UserId} resolved to Holded contact " +
            "{HoldedContactId} / account {SupplierAccountNum}, which is already bound to member " +
            "{ConflictUserId} ({ConflictSource}). Holded is authoritative so the binding was written; " +
            "both now show on /Finance/Creditors and one must be unbound.",
            writePath, userId, holdedContactId, supplierAccountNum, conflict.UserId, conflict.Source);

    /// <summary>Still exactly <paramref name="asRead"/>: same row, same content, or still absent. Compared field by
    /// field: the upsert is keyed by UserId and mutates in place, so an admin's rebind keeps the Id.</summary>
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

        // The admin's action stands and nothing is written, so this is the only record that a push was overtaken.
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
        Guid userId, int supplierAccountNum, Guid actorUserId, CancellationToken ct = default)
    {
        // The dropdown is filtered, but the number arrives on a POST. Holded numbers every supplier contact, so without
        // this an org vendor's account is bindable.
        if (!IsMemberCreditorAccount(supplierAccountNum))
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is outside the member creditor block " +
                $"({CreditorAccountMin}–{CreditorAccountMax}) — that is not a member's account.");

        // Only UserId is unique in the DB. Checked before the Holded call so a doomed bind costs no round-trip.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        if (FindConflictingBinding(bindings, userId, supplierAccountNum, holdedContactId: null) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is already bound to a different member. " +
                "Check /Finance/Creditors to see who, and unbind them there first.");

        // Refreshed first, so a contact made in Holded since the last sync binds, and the rows read it afterwards.
        await holded.SyncContactsAsync(ct);
        var contact = (await holded.GetContactsAsync(ct)).Contacts
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);
        if (contact is null)
            return CreditorBindResult.Failure(
                $"No Holded contact carries account {supplierAccountNum} — nothing bound.");

        // A member whose binding carries this contact but whose 400000xx never resolved is invisible to the check above
        // and on /Finance/Creditors (keyed by number), yet merges payables just the same. So name the contact.
        if (FindConflictingBinding(bindings, userId, supplierAccountNum: null, contact.Id) is not null)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} belongs to Holded contact \"{contact.Name}\", which is " +
                "already bound to a different member whose account number has not resolved yet. " +
                "Nothing was changed — two members must never share one Holded contact.");

        var now = clock.GetCurrentInstant();
        if (!await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contact.Id,
            SupplierAccountNum = supplierAccountNum,
            Source = CreditorContactSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct))
            return CreditorBindResult.Failure(
                "This member was bound by a concurrent action while you were binding. Reload and check " +
                "/Finance/Creditors.");
        await audit.LogAsync(FinanceAuditActions.HoldedCreditorBound, HoldedCreditorAccount, userId,
            $"Bound creditor account {supplierAccountNum} (Holded contact {contact.Id})",
            actorUserId, userId, AuditSubjectTypes.User);
        return CreditorBindResult.Success;
    }

    public async Task<CreditorLedger?> GetCreditorLedgerAsync(
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

    public async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct = default)
    {
        var contactId = await EnsureCreditorContactCoreAsync(
            userId, legalName, burnerName, iban, seedContactId, seedAccountNum, jobName, ct);
        await EnsureContactMirroredAsync(contactId, accountNum: null, ct);
        return contactId;
    }

    /// <summary>A contact a push minted or numbered must be in the mirror before the next nightly sync, or recon and
    /// /Finance/Creditors call its binding missing. One list call, and only when the mirror lacks it. A failure is
    /// logged, not thrown: the push already succeeded, and the nightly sync catches up.</summary>
    private async Task EnsureContactMirroredAsync(string contactId, int? accountNum, CancellationToken ct)
    {
        var mirrored = (await holded.GetContactsAsync(ct)).Contacts
            .FirstOrDefault(c => string.Equals(c.Id, contactId, StringComparison.Ordinal));
        if (mirrored is not null && (accountNum is null || mirrored.SupplierAccountNum == accountNum)) return;
        try
        {
            await holded.SyncContactsAsync(ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(
                "Holded contact {ContactId} is not in the contact mirror and the refresh failed; it appears after the "
                + "next sync. {Reason}", contactId, ex.Message);
        }
    }

    private async Task<string> EnsureCreditorContactCoreAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        var allBindings = await repo.GetCreditorContactsAsync(ct);

        // The seed is our guess off a prior report, not Holded's word, so it gets the manual bind's remedy: refuse and
        // let the push mint a contact. That is also what makes Unbind durable.
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

        // Never PUT a linked contact: Holded's v2 update is a full replacement, so supplier_record resets and the next
        // purchase doc mints a second creditor account. So a legal-name or IBAN change after the first push does not
        // reach Holded (open: peterdrier/Humans#1777).
        string contactId;
        var minted = false;
        if (!string.IsNullOrEmpty(binding?.HoldedContactId))
            contactId = binding.HoldedContactId;
        else if (!string.IsNullOrEmpty(seedContactId))
            contactId = seedContactId;
        else
        {
            // Burner goes in tradeName only, and only when it differs from the legal name.
            var tradeName = !string.IsNullOrWhiteSpace(burnerName)
                            && !string.Equals(burnerName, legalName, StringComparison.Ordinal)
                ? burnerName
                : null;

            contactId = await holdedWrite.CreateContactAsync(new HoldedContactInput
            {
                Name = legalName,
                TradeName = tradeName,
                Type = "creditor",
                Iban = string.IsNullOrWhiteSpace(iban) ? null : iban,
            }, ct);
            minted = true;
        }

        // A refused seed cannot collide, so anything left is a pre-existing overlap on the member's own binding: not
        // this push's doing, but reported.
        var accountNum = binding?.SupplierAccountNum ?? seedAccountNum;
        var conflict = FindConflictingBinding(allBindings, userId, accountNum, contactId);
        if (conflict is not null)
            LogBindingCollision(
                nameof(EnsureCreditorContactAsync), userId, contactId, accountNum, conflict);

        // A member already holding this contact has nothing to write but UpdatedAt, and the push is several Holded
        // calls long, so writing this copy would clobber an admin's change meanwhile. Skipping is what makes Unbind
        // hold against an in-flight push.
        if (binding is not null
            && string.Equals(binding.HoldedContactId, contactId, StringComparison.Ordinal)
            && accountNum == binding.SupplierAccountNum)
            return contactId;

        // A binding missing its number writes real content, so it cannot skip; re-reading here shrinks the window in
        // which it undoes an admin's Bind or Unbind. A contact this call just minted may have lost to a concurrent
        // drain's own, so that case checks and retracts ours if it lost.
        if (minted && binding is null)
        {
            var bound = await repo.GetCreditorContactByUserAsync(userId, ct);
            if (bound is { HoldedContactId.Length: > 0 })
                return await AdoptBoundContactAsync(userId, contactId, minted, bound, jobName, ct);
        }
        else if (!await BindingUnchangedAsync(nameof(EnsureCreditorContactAsync), userId, binding, ct))
            return contactId;

        var now = clock.GetCurrentInstant();
        var row = new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),                                   // ignored on update (keyed by UserId)
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = binding?.Source ?? CreditorContactSource.Auto, // preserve a Manual binding
            CreatedAt = now,
            UpdatedAt = now,
        };
        // A minted contact's binding is insert-only: an upsert could take its update branch over a
        // winner's binding that landed after the re-read above, and orphan the winner's contact.
        var written = minted && binding is null
            ? await repo.TryInsertCreditorContactAsync(row, ct)
            : await repo.UpsertCreditorContactAsync(row, now, ct);
        if (written) return contactId;

        // A concurrent run took the member's one binding slot between the read above and this write.
        var winner = await repo.GetCreditorContactByUserAsync(userId, ct)
            ?? throw new InvalidOperationException(
                $"Creditor binding insert for member {userId} hit the unique index but no binding exists.");
        return await AdoptBoundContactAsync(userId, contactId, minted, winner, jobName, ct);
    }

    /// <summary>A concurrent run bound <paramref name="bound"/> first, so that is the member's contact and one this
    /// call minted is deleted from Holded, audited. A failed delete is logged, not thrown; the orphan then shows on
    /// /Finance/HoldedDuplicates.</summary>
    private async Task<string> AdoptBoundContactAsync(
        Guid userId, string contactId, bool minted, HoldedCreditorContact bound, string jobName,
        CancellationToken ct)
    {
        if (!minted || string.Equals(bound.HoldedContactId, contactId, StringComparison.Ordinal))
            return bound.HoldedContactId;

        try
        {
            await holdedWrite.DeleteContactAsync(contactId, ct);
            await audit.LogAsync(FinanceAuditActions.HoldedDuplicateRemoved, AuditSubjectTypes.User, userId,
                $"DELETED duplicate Holded contact {contactId} that a concurrent push created; kept "
                + $"{bound.HoldedContactId}.",
                jobName);
            logger.LogWarning(
                "Member {UserId} was bound to Holded contact {BoundContactId} by a concurrent push; the " +
                "contact {MintedContactId} this push created was deleted from Holded.",
                userId, bound.HoldedContactId, contactId);
        }
        catch (HoldedApiException ex)
        {
            logger.LogError(ex,
                "Member {UserId} was bound to Holded contact {BoundContactId} by a concurrent push, but " +
                "deleting the duplicate contact {MintedContactId} this push created failed — delete it " +
                "in Holded by hand.",
                userId, bound.HoldedContactId, contactId);
        }
        return bound.HoldedContactId;
    }

    public async Task SetCreditorAccountNumAsync(
        Guid userId, int supplierAccountNum, string jobName, CancellationToken ct = default)
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

        // Same re-check as EnsureCreditorContactAsync: this row carries values read above, so writing it over an
        // admin's newer binding would revert it.
        if (!await BindingUnchangedAsync(nameof(SetCreditorAccountNumAsync), userId, binding, ct)) return;

        var now = clock.GetCurrentInstant();
        var written = await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = binding.Id,
            UserId = userId,
            HoldedContactId = binding.HoldedContactId,
            SupplierAccountNum = supplierAccountNum,
            Source = binding.Source,
            CreatedAt = binding.CreatedAt,
            UpdatedAt = now,
        }, now, ct);

        // Which account a member is paid from is the Board's business, and this write moves it on Holded's word alone,
        // so every change is audited, a first resolution included.
        if (!written || binding.SupplierAccountNum == supplierAccountNum) return;
        await EnsureContactMirroredAsync(binding.HoldedContactId, supplierAccountNum, ct);
        var previous = binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)";
        await audit.LogAsync(FinanceAuditActions.HoldedCreditorAccountNumChanged, HoldedCreditorAccount, userId,
            $"Creditor account changed from {previous} to {supplierAccountNum} (Holded contact "
            + $"{binding.HoldedContactId}), as Holded reported after the push",
            jobName, userId, AuditSubjectTypes.User);
    }

    public async Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        // The audit names the row the delete removed, not an earlier read a push could have overtaken.
        if (await DeleteCreditorBindingAsync(userId, ct) is not { } binding) return false;

        await audit.LogAsync(FinanceAuditActions.HoldedCreditorUnbound, HoldedCreditorAccount, userId,
            $"Unbound creditor account {binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)"} "
            + $"(Holded contact {binding.HoldedContactId})",
            actorUserId, userId, AuditSubjectTypes.User);
        return true;
    }

    private async Task<HoldedCreditorContact?> DeleteCreditorBindingAsync(Guid userId, CancellationToken ct)
    {
        var removed = await repo.DeleteCreditorContactAsync(userId, ct);
        if (removed is not null)
            logger.LogInformation("Cleared the creditor binding for member {UserId}.", userId);
        return removed;
    }

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
        if (await ContactsByIdAsync(ct) is not { } contactById)
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

    /// <summary>The row is kept and flagged; nothing pairs, books or counts it again. Only an unbooked row with no
    /// Holded posting can be withdrawn.</summary>
    public Task<SepaBookingResult> WithdrawSepaTransferAsync(
        Guid transferId, Guid actorUserId, CancellationToken ct = default) =>
        booking.UnderBookingGateAsync(() => WithdrawSepaTransferGatedAsync(transferId, actorUserId, ct));

    private async Task<SepaBookingResult> WithdrawSepaTransferGatedAsync(
        Guid transferId, Guid actorUserId, CancellationToken ct)
    {
        var transfer = await repo.GetSepaTransferAsync(transferId, ct);
        if (transfer is null)
            return new SepaBookingResult(false, "That transfer no longer exists — reload the page.");
        if (WithdrawRefusal(transfer) is { } refusal)
            return new SepaBookingResult(false, refusal);

        var row = (await repo.GetSepaPayoutTransferRowsAsync(ct)).First(r => r.TransferId == transferId);
        var now = clock.GetCurrentInstant();
        await repo.SaveSepaTransfersWithdrawnAsync([transferId], now, actorUserId, ct);

        await audit.LogAsync(
            FinanceAuditActions.SepaPayoutTransferWithdrawn, SepaTransferEntityType, transferId,
            $"WITHDRAWN the SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
            + $"{transfer.SupplierAccountNum} (file {row.FileName}): cancelled at the bank before execution.",
            actorUserId, transfer.UserId, AuditSubjectTypes.User);

        await payoutEmails.SendWithdrawnEmailAsync(transfer, booking.FileDate(row));

        return new SepaBookingResult(true,
            $"Withdrew the {Euros(transfer.Amount)} transfer to {transfer.IbanMasked} from {row.FileName}; "
            + "the member has been told and the account is payable again.");
    }

    /// <summary>For a file the bank rejected whole: withdraws every transfer still live in it, in one save, so it is
    /// cancelled. All or nothing: refused if any live transfer could not be withdrawn on its own. No member email,
    /// unlike a single withdrawal: the file is usually reissued at once, and that sends the payout email again.</summary>
    public Task<SepaBookingResult> CancelSepaFileAsync(Guid fileId, Guid actorUserId, CancellationToken ct = default) =>
        booking.UnderBookingGateAsync(() => CancelSepaFileGatedAsync(fileId, actorUserId, ct));

    private async Task<SepaBookingResult> CancelSepaFileGatedAsync(Guid fileId, Guid actorUserId, CancellationToken ct)
    {
        var rows = (await repo.GetSepaPayoutTransferRowsAsync(ct)).Where(r => r.FileId == fileId).ToList();
        if (rows.Count == 0)
            return new SepaBookingResult(false, "That file no longer exists — reload the page.");
        if (CancelFileRefusal(rows) is { } refusal)
            return new SepaBookingResult(false, refusal);

        var live = rows.Where(r => !r.IsWithdrawn).ToList();
        var fileName = rows[0].FileName;
        await repo.SaveSepaTransfersWithdrawnAsync(live.Select(r => r.TransferId).ToList(),
            clock.GetCurrentInstant(), actorUserId, ct);

        foreach (var r in live)
            await audit.LogAsync(
                FinanceAuditActions.SepaPayoutTransferWithdrawn, SepaTransferEntityType, r.TransferId,
                $"WITHDRAWN the SEPA payout {Euros(r.Amount)} to {r.IbanMasked} on creditor account "
                + $"{r.SupplierAccountNum}: file {fileName} was cancelled whole, the bank never executed it.",
                actorUserId, r.UserId, AuditSubjectTypes.User);

        return new SepaBookingResult(true,
            $"Cancelled {fileName}: {live.Count} transfers, {Euros(live.Sum(r => r.Amount))}. "
            + "Their accounts are payable again; no member was emailed.");
    }

    /// <summary>The same rule decides whether <c>/Finance/Sepa</c> offers Cancel file.</summary>
    internal static string? CancelFileRefusal(IReadOnlyCollection<SepaPayoutTransferRow> fileRows)
    {
        var live = fileRows.Where(r => !r.IsWithdrawn).ToList();
        if (live.Count == 0) return "This file is already cancelled.";
        return live.Select(r => WithdrawRefusal(r)).FirstOrDefault(r => r is not null) is { } refusal
            ? $"Cannot cancel the file: {refusal}"
            : null;
    }

    /// <summary>The same rule shows the reason on <c>/Finance/Sepa</c> in place of the button.</summary>
    internal static string? WithdrawRefusal(SepaPayoutTransferRow row) =>
        WithdrawRefusal(row.IsWithdrawn, row.IsBooked, row.HoldedBankMovementId, row.HoldedPaymentRefs, row.IbanMasked);

    private static string? WithdrawRefusal(SepaPayoutTransfer t) =>
        WithdrawRefusal(t.WithdrawnAt is not null, t.BookedAt is not null, t.HoldedBankMovementId, t.HoldedPaymentRefs, t.IbanMasked);

    private static string? WithdrawRefusal(
        bool withdrawn, bool booked, string? bankMovementId, string? paymentRefs, string ibanMasked)
    {
        if (withdrawn) return $"Transfer to {ibanMasked} is already withdrawn.";
        if (booked) return $"Transfer to {ibanMasked} is booked in Holded — the money moved, so it cannot be withdrawn.";
        if (bankMovementId is { Length: > 0 })
            return $"Transfer to {ibanMasked} is tied to Sabadell line {bankMovementId} — it cannot be withdrawn.";
        if (paymentRefs is { Length: > 0 })
            return $"Transfer to {ibanMasked} already posted Holded payment(s) {paymentRefs} — the money moved, so it cannot be withdrawn; book it to finish.";
        return null;
    }

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
    private static Dictionary<Guid, IReadOnlySet<string>> MemberContacts(
        IReadOnlyList<ExpenseHoldedBooking> bookings, IReadOnlyList<HoldedCreditorContact> bindings,
        IReadOnlyDictionary<string, HoldedContactDto> contactById) =>
        HoldedDuplicateMatcher.MemberContacts(
            bookings.Select(b => (b.CreditorUserId, b.HoldedContactId)), bindings,
            id => contactById.GetValueOrDefault(id)?.Name,
            contactById.Values
                .Where(c => IsMemberCreditorAccount(c.SupplierAccountNum)
                            && !string.IsNullOrWhiteSpace(c.Name))
                .ToLookup(c => c.Name!.Trim(), c => c.Id, StringComparer.OrdinalIgnoreCase));

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
        if (await ContactsByIdAsync(ct) is not { } contactById)
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
            var contacts = await ContactsByIdAsync(ct)
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
        DeleteCreditorBindingAsync(userId, ct);


}
