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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;
using static Humans.Finance.Services.SepaText;

namespace Humans.Finance.Services;

internal sealed class Service(
    IHoldedRepository repo,
    IHoldedClient client,
    IBudgetService budget,
    IHoldedService holded,
    IExpenseHoldedBookingService expenses,
    IClock clock,
    IMemoryCache cache,
    IAuditLogService audit,
    IUserService users,
    IUserEmailService userEmails,
    IEmailService emailService,
    FinanceEmails emails,
    IOptions<SepaOptions> sepa,
    TrackedCache<string, Dictionary<string, int>> bookingState,
    ILogger<Service> logger) : IHoldedFinanceManagement, IHoldedFinanceAdminService, ISepaBankBooking, IUserDataContributor
{
    internal const string HoldedCreditorAccount = "HoldedCreditorAccount";
    internal const string AdditionalHoldedCreditorAccounts = "AdditionalHoldedCreditorAccounts";
    internal const string SepaPayouts = "SepaPayouts";
    internal const string CreditorLedger = "CreditorLedger";

    /// <summary>First number of the P&amp;L expense block both provisioning paths allocate from.</summary>
    internal const int ExpenseAccountBlockStart = 62900100;
    internal const string HoldedExpenseAccount = "HoldedExpenseAccount";

    private static readonly TimeSpan ContactsCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private readonly SepaBankBooking booking = new(repo, client, clock, bookingState, audit, sepa, logger);
    private readonly HoldedDocumentSync documentSync = new(repo, client, clock, logger);
    private readonly SepaPayoutEmails payoutEmails = new(users, userEmails, emailService, emails, logger);

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var year = await budget.GetActiveYearAsync(ct);
        var categories = year is null
            ? Array.Empty<(Guid Id, string Name, string Group)>()
            : year.Groups
                  .SelectMany(g => g.Categories.Select(c => (c.Id, c.Name, Group: g.Name)))
                  .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map
            .Where(m => m.IsActive)
            .ToDictionary(m => m.BudgetCategoryId);

        // Avoid numbers in BOTH the local map and the live chart, so one taken remotely but missing locally (a failed
        // map write, or an account made directly in Holded) is never re-proposed.
        var remoteAccounts = await client.ListExpenseAccountsAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.AccountNum))
            .ToHashSet();

        var rows = new List<HoldedProvisioningRow>();
        // Seeded up front, not as the walk meets them: otherwise a ToAdd category sorting before a mapped one could be
        // handed its tag, and two active rows sharing a tag make attribution arbitrary.
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

    public async Task<int> ProvisionAsync(
        int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default)
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
                // Audited as soon as Holded holds the account: a failed map save below must not hide it.
                await audit.LogAsync(AuditAction.HoldedCategoryAccountProvisioned, HoldedExpenseAccount,
                    row.BudgetCategoryId,
                    $"Provisioned Holded expense account {row.ProposedAccountNum.Value} '{accountName}'",
                    actorUserId);
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

    public async Task<HoldedExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        IReadOnlyList<HoldedExpenseAccountDto> chart;
        try
        {
            chart = await client.ListExpenseAccountsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the Holded expense chart while creating or linking account '{Name}'", name);
            throw;
        }

        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);

        HoldedExpenseAccountDto? found;
        if (existingAccountNum is { } num)
        {
            found = chart.FirstOrDefault(a => a.AccountNum == num)
                ?? throw new InvalidOperationException($"Holded has no expense account {num}.");
        }
        else
        {
            var wanted = HoldedMatcher.NormalizeAccountName(name);
            found = chart.FirstOrDefault(a => string.Equals(HoldedMatcher.NormalizeAccountName(a.Name), wanted, StringComparison.Ordinal));
        }

        var now = clock.GetCurrentInstant();
        if (found is not null)
        {
            var isCategoryAccount = map.Any(m => m.HoldedAccountNumber == found.AccountNum);
            if (!isCategoryAccount)
                await RegisterManagedAsync(found.AccountNum, found.Id, found.Name, managed, now, ct);
            await audit.LogAsync(AuditAction.HoldedExpenseAccountLinked, HoldedExpenseAccount, Guid.Empty,
                $"Linked Holded expense account {found.AccountNum} '{found.Name}' for '{name}'", "Finance");
            return new HoldedExpenseAccountRef(found.AccountNum, found.Id, found.Name, Created: false);
        }

        var used = map.Select(m => m.HoldedAccountNumber)
            .Concat(managed.Select(m => m.HoldedAccountNumber))
            .Concat(chart.Select(a => a.AccountNum))
            .ToHashSet();
        var next = ExpenseAccountBlockStart;
        while (used.Contains(next)) next++;

        string id;
        try
        {
            id = await client.CreateExpenseAccountAsync(next, name, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Holded expense account {AccountNum} '{Name}'", next, name);
            throw;
        }

        await RegisterManagedAsync(next, id, name, managed, now, ct);
        await audit.LogAsync(AuditAction.HoldedExpenseAccountCreated, HoldedExpenseAccount, Guid.Empty,
            $"Created Holded expense account {next} '{name}'", "Finance");
        return new HoldedExpenseAccountRef(next, id, name, Created: true);
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
        await audit.LogAsync(AuditAction.HoldedExpenseAccountActiveChanged, HoldedExpenseAccount, Guid.Empty,
            $"{(isActive ? "Restored" : "Retired")} Holded expense account {accountNum} '{row.Label}'", "Finance");
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var year = await budget.GetActiveYearAsync(ct);
        var labels = year?.Groups
            .SelectMany(g => g.Categories.Select(c => (c.Id, Label: $"{g.Name} / {c.Name}")))
            .ToDictionary(x => x.Id, x => x.Label)
            ?? new Dictionary<Guid, string>();

        // A map row whose category is not in the active year still labels old reports but takes no new booking, so it
        // is inactive here and dropped with activeOnly.
        var options = map
            .Where(m => m.IsActive)
            .Select(m => new HoldedExpenseAccountOption(
                m.HoldedAccountNumber, m.HoldedAccountId,
                labels.GetValueOrDefault(m.BudgetCategoryId, $"Category {m.HoldedAccountNumber}"),
                IsBudgetCategory: true, IsActive: labels.ContainsKey(m.BudgetCategoryId)))
            .Where(o => !activeOnly || o.IsActive)
            .Concat(managed
                .Where(m => !activeOnly || m.IsActive)
                .Select(m => new HoldedExpenseAccountOption(
                    m.HoldedAccountNumber, m.HoldedAccountId, m.Label,
                    IsBudgetCategory: false, IsActive: m.IsActive)))
            .OrderBy(o => o.AccountNum)
            .ToList();
        return options;
    }

    public Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default) =>
        documentSync.SyncAsync(LogDuplicateDocumentsAsync, ct);

    public async Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default)
    {
        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        // From the repo, so /Holded never builds the full creditor view (a live contacts walk) just to show a number.
        var bindings = await repo.GetCreditorContactsAsync(ct);
        return new HoldedDocSyncInfo(
            state.LastSyncAt, state.Status, state.LastError, state.LastSyncedDocCount, bindings.Count);
    }

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
                        d.HoldedDocId, d.DocNumber, d.ContactName, d.Description, d.Date, d.Total,
                        HoldedDocUrl(d.HoldedDocId)))
                    .ToList()))
            .Where(r => r.Actual != 0m)
            .ToList();
    }

    public async Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var docs = await repo.GetUnmatchedAsync(ct);
        return docs
            .Select(d => new HoldedUnmatchedRow(
                d.HoldedDocId,
                d.DocNumber,
                d.ContactName,
                d.Description,
                d.Total,
                ReasonFor(d),
                HoldedDocUrl(d.HoldedDocId)))
            .ToList();
    }

    // Holded has no stable per-doc page; its purchase list opens a doc from this fragment.
    private static string HoldedDocUrl(string holdedDocId) =>
        $"https://app.holded.com/expenses/list#open:purchase-{holdedDocId}";

    public async Task<string?> GetHoldedAccountIdForCategoryAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        return map.FirstOrDefault(m => m.IsActive && m.BudgetCategoryId == budgetCategoryId)?.HoldedAccountId;
    }

    /// <summary>Category and group names resolved from the active budget year.</summary>
    private async Task<IReadOnlyList<HoldedCategoryMapRow>> BuildCategoryMapAsync(CancellationToken ct)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var categories = await ActiveYearCategoriesAsync(ct);

        return map.Select(m => new HoldedCategoryMapRow(
            m.BudgetCategoryId,
            categories.TryGetValue(m.BudgetCategoryId, out var c) ? c.Name : null,
            categories.TryGetValue(m.BudgetCategoryId, out var g) ? g.Group : null,
            m.HoldedAccountNumber,
            m.HoldedAccountId,
            m.Tag,
            m.IsActive,
            m.UpdatedAt)).ToList();
    }

    public Task<IReadOnlyList<HoldedCategoryMapRow>> GetCategoryMapAsync(CancellationToken ct = default) =>
        BuildCategoryMapAsync(ct);

    /// <summary>Empty when no year is active.</summary>
    private async Task<Dictionary<Guid, (string Name, string Group)>> ActiveYearCategoriesAsync(CancellationToken ct)
    {
        var year = await budget.GetActiveYearAsync(ct);
        return year is null
            ? new Dictionary<Guid, (string Name, string Group)>()
            : year.Groups
                .SelectMany(g => g.Categories.Select(c => (c.Id, Name: c.Name, Group: g.Name)))
                .ToDictionary(c => c.Id, c => (c.Name, c.Group));
    }

    public async Task<HoldedConnectorVm> GetConnectorOverviewAsync(CancellationToken ct = default)
    {
        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var categoryMap = await BuildCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var docs = await repo.GetAllDocsAsync(ct);

        // A doc pointing outside the active year keeps a null name rather than a lookup per row.
        var categories = await ActiveYearCategoriesAsync(ct);

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
                // Never having run is stale too: it leaves the actuals and the unmatched queue just as wrong.
                IsStale: age is null || age >= HoldedDocSyncVm.StaleAfter,
                // A failed run moves only this, so it is what dates an Error.
                state.StatusChangedAt),
            bindings.Count,
            categoryMap,
            managed.OrderBy(m => m.HoldedAccountNumber)
                .Select(m => new HoldedManagedAccountVm(m.HoldedAccountNumber, m.HoldedAccountId, m.Label, m.IsActive, m.CreatedAt))
                .ToList(),
            docs.Select(d => new HoldedDocVm(
                d.HoldedDocId,
                d.DocNumber,
                d.ContactName,
                d.Description,
                HoldedDocUrl(d.HoldedDocId),
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

    // A new expense-only contact gets type "creditor", which Holded numbers in the 410 series rather than the
    // 400-series proveedor accounts older members carry, so the block spans both. Bindings, not the range, tell a
    // member's account from an org vendor's.
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

    // Balance = Σdebit − Σcredit, so negative means the organisation owes the member.
    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLineInfo> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    public async Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts,
                       IReadOnlyList<CreditorContactBinding> Unresolved)> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        var byAccount = await holded.GetAccountBalancesAsync(ct: ct);

        // Not range-filtered: the range decides which accounts are creditor accounts, not which contact a binding names.
        var contactById = await ContactsByIdAsync(ct);

        // The range filter is load-bearing: Holded numbers every supplier contact, so unfiltered an org vendor becomes
        // bindable. Group-by-first so a duplicate number cannot throw.
        var contacts = contactById.Values
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax)
            .GroupBy(c => c.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Holded's contact, not the binding's cached number, decides the row (the stored number is the fallback), so an
        // unresolved binding still reaches its account and two bindings on one contact show as a collision. One
        // partition of one snapshot, so no binding is both on a row and on the unresolved card.
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
                    .OrderBy(x => x.Binding.CreatedAt)
                    .Select(x => ToBinding(x.Binding))
                    .ToList());

        // No number of our own and none on Holded's contact. Nothing retries these, so they are returned, not
        // dropped: unreturned is unbindable.
        var unresolved = resolved
            .Where(x => x.Account is null)
            .OrderBy(x => x.Binding.CreatedAt)
            .Select(x => ToBinding(x.Binding))
            .ToList();

        // Same OpenDocs filter the booking applies. One list call per render; a vendor failure costs the column. The
        // list also feeds the duplicate check: an inflated balance makes the row unpayable, and null (unreadable) is
        // refused too.
        Dictionary<string, int>? openDocsByContact = null;
        HashSet<Guid>? flaggedUsers = null;
        try
        {
            var purchaseDocs = await client.ListPurchaseDocumentsAsync(ct);
            openDocsByContact = SepaBankBooking.OpenDocs(purchaseDocs)
                .Where(d => !string.IsNullOrEmpty(d.ContactId))
                .GroupBy(d => d.ContactId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            flaggedUsers = (await FindDuplicateDocumentsAsync(purchaseDocs, contactById, ct))
                .Select(m => m.Booking.CreditorUserId)
                .ToHashSet();
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded purchase documents unavailable; /Finance/Creditors shows no open-document counts and cannot rule out duplicates.");
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
            .Where(num => num is >= CreditorAccountMin and <= CreditorAccountMax)
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

                return new HoldedCreditorAccountRow(
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
            }).ToList();

        // Accounts but no names at all means an unusable contact list, leaving the bind card as bare numbers. One
        // missing name is a real gap in Holded, so only the all-or-nothing case is logged.
        if (rows.Count > 0 && rows.TrueForAll(r => string.IsNullOrWhiteSpace(r.Name)))
            logger.LogWarning(
                "None of the {Count} creditor accounts resolved a Holded contact name; the bind card and " +
                "/Finance/Creditors will show bare account numbers.", rows.Count);

        return (rows, unresolved);
    }

    private static int? ResolveCreditorAccount(
        HoldedCreditorContact binding, IReadOnlyDictionary<string, HoldedContactDto> contacts)
    {
        var account = contacts.GetValueOrDefault(binding.HoldedContactId)?.SupplierAccountNum;
        return account is >= CreditorAccountMin and <= CreditorAccountMax ? account : binding.SupplierAccountNum;
    }

    /// <summary>Keyed by contact id, the identity a binding stores. The rows and the payout both resolve through this
    /// one map, so a row's name and IBAN cannot disagree with the file's.</summary>
    private async Task<Dictionary<string, HoldedContactDto>> ContactsByIdAsync(CancellationToken ct) =>
        (await ListContactsOrEmptyAsync(ct))
        .GroupBy(c => c.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Cached because every /Finance/Creditors and /Expenses/{id} load reads the same list. A vendor failure
    /// costs the names, not the page; anything else is a bug and throws.</summary>
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
        if (supplierAccountNum is < CreditorAccountMin or > CreditorAccountMax)
            return CreditorBindResult.Failure(
                $"Account {supplierAccountNum} is outside the member creditor block " +
                $"({CreditorAccountMin}–{CreditorAccountMax}) — that is not a member's account.");

        // Only UserId is unique in the DB. Checked before the Holded call so a doomed bind costs no round-trip.
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
        await audit.LogAsync(AuditAction.HoldedCreditorBound, HoldedCreditorAccount, userId,
            $"Bound creditor account {supplierAccountNum} (Holded contact {contact.Id})",
            actorUserId, userId, nameof(User));
        return CreditorBindResult.Success;
    }

    public async Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(
        int supplierAccountNum, CancellationToken ct = default)
    {
        // Mirror lines and the cached contact list: no Holded call.
        var lines = await holded.GetLedgerLinesAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        var contacts = await ContactsByIdAsync(ct);
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
        return new HoldedCreditorLedger(
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
                : new HoldedContactInfo(
                    contact.Name, contact.TradeName, contact.Email, contact.Phone,
                    contact.Mobile, contact.Iban, contact.TaxCode, contact.Address));
    }

    public async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct = default)
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

            contactId = await client.CreateContactAsync(new HoldedContactInput
            {
                Name = legalName,
                TradeName = tradeName,
                CustomId = userId.ToString(),
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
            await client.DeleteContactAsync(contactId, ct);
            await audit.LogAsync(AuditAction.HoldedDuplicateRemoved, nameof(User), userId,
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
        var previous = binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)";
        await audit.LogAsync(AuditAction.HoldedCreditorAccountNumChanged, HoldedCreditorAccount, userId,
            $"Creditor account changed from {previous} to {supplierAccountNum} (Holded contact "
            + $"{binding.HoldedContactId}), as Holded reported after the push",
            jobName, userId, nameof(User));
    }

    public async Task<bool> ClearCreditorContactAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        // The audit names the row the delete removed, not an earlier read a push could have overtaken.
        if (await DeleteCreditorBindingAsync(userId, ct) is not { } binding) return false;

        await audit.LogAsync(AuditAction.HoldedCreditorUnbound, HoldedCreditorAccount, userId,
            $"Unbound creditor account {binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)"} "
            + $"(Holded contact {binding.HoldedContactId})",
            actorUserId, userId, nameof(User));
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

        // The unmasked IBAN goes nowhere but the file and the payout record. Keyed by contact id, not account number:
        // two contacts can share one 400000xx, and only the bound one is the member's.
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

            // Re-derived here because the posted selection is never trusted.
            if (row.HasDuplicateDocuments is null)
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum}: Holded's document list could not be read, so "
                    + "duplicate documents cannot be ruled out — try again later.");
            if (row.HasDuplicateDocuments == true)
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum} has duplicate documents in Holded — remove them "
                    + "on /Finance/HoldedDuplicates first.");
            if (row.PendingPayoutFileName is { } pendingFile)
                return SepaPayoutResult.Failure(
                    $"Creditor account {s.SupplierAccountNum} is already in payout file {pendingFile}, which is "
                    + "not booked yet — book it or withdraw the transfer first.");

            // Name and IBAN both come off the bound contact, so no transfer pairs one person's name with another's
            // account.
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
            return SepaPayoutResult.Failure(ex.Message);
        }

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
                AuditAction.SepaPayoutTransfer, SepaTransferEntityType, t.Id,
                $"SEPA payout {Euros(t.Amount)} to {t.IbanMasked} on creditor account "
                + $"{t.SupplierAccountNum} (file {fileName}).",
                actorUserId, t.UserId, nameof(User));

        // The payout is committed: abandoning the download must not suppress its notifications.
        await payoutEmails.SendPayoutEmailsAsync(transfers);

        return new SepaPayoutResult(fileName, xml, null);
    }

    /// <summary>The row is kept and flagged; nothing pairs, books or counts it again. Only an unbooked row with no
    /// Holded posting can be withdrawn.</summary>
    public async Task<SepaBookingResult> WithdrawSepaTransferAsync(
        Guid transferId, Guid actorUserId, CancellationToken ct = default)
    {
        var transfer = await repo.GetSepaTransferAsync(transferId, ct);
        if (transfer is null)
            return new SepaBookingResult(false, "That transfer no longer exists — reload the page.");
        if (WithdrawRefusal(transfer) is { } refusal)
            return new SepaBookingResult(false, refusal);

        var row = (await repo.GetSepaPayoutTransferRowsAsync(ct)).First(r => r.TransferId == transferId);
        var now = clock.GetCurrentInstant();
        await repo.SaveSepaTransferWithdrawnAsync(transferId, now, actorUserId, ct);

        await audit.LogAsync(
            AuditAction.SepaPayoutTransferWithdrawn, SepaTransferEntityType, transferId,
            $"WITHDRAWN the SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
            + $"{transfer.SupplierAccountNum} (file {row.FileName}): cancelled at the bank before execution.",
            actorUserId, transfer.UserId, nameof(User));

        await payoutEmails.SendWithdrawnEmailAsync(transfer, booking.FileDate(row));

        return new SepaBookingResult(true,
            $"Withdrew the {Euros(transfer.Amount)} transfer to {transfer.IbanMasked} from {row.FileName}; "
            + "the member has been told and the account is payable again.");
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
            return $"Transfer to {ibanMasked} already posted Holded payment(s) {paymentRefs} — settle it in Holded instead.";
        return null;
    }

    public Task<(IReadOnlyList<SepaPayoutTransferRow> Transfers, string? UnavailableReason)>
        GetSepaTransfersAsync(CancellationToken ct = default) => booking.GetSepaTransfersAsync(ct);

    public Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError)>
        GetSepaPayoutsAsync(int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays,
            CancellationToken ct = default) => booking.GetSepaPayoutsAsync(toleranceDays, ct);

    public Task RunAsync(CancellationToken ct = default) => booking.RunAsync(ct);

    public Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        booking.BookSepaTransferAsync(transferId, bankMovementId, actorUserId, toleranceDays);

    public Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        booking.BookSepaFileAsync(fileId, bankMovementId, actorUserId, toleranceDays);

    public Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId,
        int toleranceDays = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays) =>
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

    /// <summary>The nightly check: the list-level rule (description and total, no notes GET), logged once. A false
    /// positive only refuses a payout. Never fails the document sync.</summary>
    private async Task LogDuplicateDocumentsAsync(IReadOnlyList<HoldedPurchaseDocListItemDto> docs, CancellationToken ct)
    {
        try
        {
            var matches = await FindDuplicateDocumentsAsync(docs, await ContactsByIdAsync(ct), ct);
            if (matches.Count == 0)
            {
                logger.LogInformation("Holded duplicate check: zero expense lines match more than one purchase document.");
                return;
            }
            logger.LogWarning(
                "Holded duplicate check: {Lines} expense line(s) match more than one purchase document — "
                + "{Docs} extra document(s) totalling {Amount} across {Members} member(s). Review them on "
                + "/Finance/HoldedDuplicates; their creditor accounts are not payable until they are removed.",
                matches.Count, matches.Sum(m => m.Duplicates.Count),
                Euros(matches.Sum(m => m.Duplicates.Sum(d => d.Total))),
                matches.Select(m => m.Booking.CreditorUserId).Distinct().Count());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Holded duplicate check failed; the document sync continues without it.");
        }
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
    private static Dictionary<Guid, IReadOnlySet<string>> MemberContacts(
        IReadOnlyList<ExpenseHoldedBooking> bookings, IReadOnlyList<HoldedCreditorContact> bindings,
        IReadOnlyDictionary<string, HoldedContactDto> contactById)
    {
        var boundUserByContact = bindings
            .Where(b => !string.IsNullOrEmpty(b.HoldedContactId))
            .GroupBy(b => b.HoldedContactId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.Ordinal);
        var bindingByUser = bindings.ToDictionary(b => b.UserId);
        var byName = contactById.Values
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax
                        && !string.IsNullOrWhiteSpace(c.Name))
            .ToLookup(c => c.Name!.Trim(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<Guid, IReadOnlySet<string>>();
        foreach (var group in bookings.GroupBy(b => b.CreditorUserId))
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (bindingByUser.GetValueOrDefault(group.Key)?.HoldedContactId is { Length: > 0 } bound) set.Add(bound);
            set.UnionWith(group.Where(b => !string.IsNullOrEmpty(b.HoldedContactId)).Select(b => b.HoldedContactId!));
            foreach (var id in set.ToList())
                if (contactById.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c.Name))
                    foreach (var twin in byName[c.Name.Trim()])
                        if (!boundUserByContact.TryGetValue(twin.Id, out var other) || other == group.Key)
                            set.Add(twin.Id);
            result[group.Key] = set;
        }
        return result;
    }

    public async Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default)
    {
        try
        {
            return await BuildDuplicatePlanAsync(ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded could not be read for /Finance/HoldedDuplicates.");
            return new HoldedDuplicatesVm([], [], "Holded could not be read, so nothing can be judged: " + ex.Message);
        }
    }

    /// <summary>
    /// Live reads, never a stored plan. Each candidate Holded would let go is fetched once for its notes and the claim
    /// re-run, so a candidate goes to the line whose notes it carries and only an exact match can be deleted. A failed
    /// fetch fails the whole plan.
    /// </summary>
    private async Task<HoldedDuplicatesVm> BuildDuplicatePlanAsync(CancellationToken ct)
    {
        var docs = await client.ListPurchaseDocumentsAsync(ct);
        var contactById = await ContactsByIdAsync(ct);
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var bookings = await expenses.GetHoldedBookingsAsync(ct);
        var contactsByUser = MemberContacts(bookings, bindings, contactById);
        var bindingByUser = bindings.ToDictionary(b => b.UserId);
        var docsByContact = docs.Where(d => !string.IsNullOrEmpty(d.ContactId))
            .ToLookup(d => d.ContactId!, StringComparer.Ordinal);

        var candidates = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser);
        var notesById = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var d in candidates.SelectMany(m => m.Duplicates))
            if (DeleteRefusal(d) is null)
                notesById[d.Id] = (await client.GetPurchaseDocumentAsync(d.Id, ct)).Notes;
        var verified = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser,
            (b, d) => HoldedDuplicateMatcher.Matches(b, d)
                      && notesById.TryGetValue(d.Id, out var notes)
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
                .Select(d => new HoldedDuplicateRefusal(d.Id, d.Total, DeleteRefusal(d) ?? "notes do not match the push — judge it by hand"))
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
                    contact?.SupplierAccountNum ?? binding?.SupplierAccountNum,
                    lines, orphans);
            })
            .OrderBy(m => m.SupplierAccountNum ?? int.MaxValue)
            .ThenBy(m => m.ContactName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Holded has no archive call, so an empty unbound contact is listed, not touched.
        var boundContacts = bindings.Select(b => b.HoldedContactId).ToHashSet(StringComparer.Ordinal);
        var empty = contactById.Values
            .Where(c => c.SupplierAccountNum is >= CreditorAccountMin and <= CreditorAccountMax
                        && !boundContacts.Contains(c.Id) && !docsByContact[c.Id].Any())
            .OrderBy(c => c.SupplierAccountNum)
            .Select(c => $"{c.SupplierAccountNum} {c.Name} ({c.Id})")
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
            plan = await BuildDuplicatePlanAsync(ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded could not be read; no duplicate was deleted.");
            return new HoldedDuplicateApplyResult([], "Holded could not be read — nothing was deleted: " + ex.Message, null);
        }
        if (!plan.HasWork)
            return new HoldedDuplicateApplyResult([], "No duplicate document was found — nothing was deleted.", null);

        var before = await ReadCreditorBalancesAsync(ct);
        var results = new List<HoldedDuplicateMemberResult>();
        foreach (var member in plan.Members)
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
                        await client.DeletePurchaseDocumentAsync(docId, ct);
                        deletedDocs.Add(docId);
                        await audit.LogAsync(
                            AuditAction.HoldedDuplicateRemoved, ExpenseLineEntityType, line.LineId,
                            $"DELETED duplicate Holded purchase document {docId} ({Euros(line.Amount)}, "
                            + $"'{line.Description}') of {member.ContactName} on creditor account {account}; "
                            + $"kept {line.KeptDocId}.",
                            actorUserId, member.UserId, nameof(User));
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
                        await client.DeleteContactAsync(contactId, ct);
                        deletedContacts.Add(contactId);
                        await audit.LogAsync(
                            AuditAction.HoldedDuplicateRemoved, nameof(User), member.UserId,
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
            await holded.SyncLedgerAsync(ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The ledger sync after removing Holded duplicates failed; the deletions stand.");
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
            return (await client.ListAccountingAccountsAsync(ct))
                .Where(a => a.Number is >= CreditorAccountMin and <= CreditorAccountMax)
                .GroupBy(a => a.Number)
                .ToDictionary(g => g.Key, g => g.First().Balance);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Holded's chart of accounts could not be read for the duplicate removal balances.");
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
        var contacts = bindings.Count == 0
            ? new Dictionary<string, HoldedContactDto>(StringComparer.Ordinal)
            : await ContactsByIdAsync(ct);
        var ledgerLines = new List<CreditorLedgerLine>();
        if (bindings.Count > 0)
        {
            // Ledger lines carry no user id, so an account another member is also bound to would leak their lines.
            // Withheld until an admin resolves the collision.
            var ownIds = bindings.Select(b => b.UserId).ToHashSet();
            var shared = (await repo.GetCreditorContactsAsync(ct))
                .Where(b => !ownIds.Contains(b.UserId))
                .Select(b => ResolveCreditorAccount(b, contacts))
                .OfType<int>()
                .ToHashSet();
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

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HoldedCreditorAccount] = null,
            [AdditionalHoldedCreditorAccounts] = null,
            [SepaPayouts] = PayoutRetention,
            [CreditorLedger] = LedgerRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The invoices live in Holded as fiscal records; the payout files survive on the same basis (see
    /// <see cref="PayoutRetention"/>).
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        DeleteCreditorBindingAsync(userId, ct);

    /// <summary>Dash-free. On a collision, appends the first 4 hex chars of the category id.</summary>
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
