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
using Humans.Base.Interfaces;
using Humans.Base.Constants;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Humans.Finance.Services;

internal sealed class Service(
    IOrgTimeZone orgTimeZone,
    IFinanceRepository repo,
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
    ILogger<Service> logger) : IFinanceService, IFinanceWrite, IFinanceAdminService, IUserDataContributor, IUserMerge
{
    public string? BankTreasuryAccountId =>
        string.IsNullOrWhiteSpace(sepa.Value.TreasuryAccountId) ? null : sepa.Value.TreasuryAccountId;

    public Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default) =>
        paymentDetails.GetIbanAsync(userId, ct);

    public Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default) =>
        paymentDetails.SetIbanAsync(userId, iban, ct);

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
        // Lines carry the merge survivor; a binding still on a merged-away id is theirs, behind their own.
        var survivors = await users.GetUserInfosAsync(bindings.Select(b => b.UserId).ToHashSet(), ct);
        var accountByUser = bindings
            .Select(b => (UserId: survivors.GetValueOrDefault(b.UserId)?.Id ?? b.UserId, Own: b.UserId,
                Account: contacts.GetValueOrDefault(b.HoldedContactId)?.SupplierAccountNum))
            .Where(x => IsMemberCreditorAccount(x.Account))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Own == g.Key).Account!.Value);

        var ledger = new Dictionary<int, IReadOnlyList<HoldedLedgerLineInfo>>();
        foreach (var account in balances.Keys
                     .Where(n => IsMemberCreditorAccount(n))
                     .Union(accountByUser.Values))
            ledger[account] = await holded.GetLedgerLinesAsync(account, ct);

        var transfers = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var members = await users.GetUserInfosAsync(expected.Select(l => l.CreditorUserId), ct);
        return ExpenseRecon.Build(
            year, syncedAt, expected, accountByUser, ledger, transfers,
            id => members.GetValueOrDefault(id)?.BurnerName ?? id.ToString(), OrgZone);
    }

    internal const string HoldedExpenseAccount = "HoldedExpenseAccount";

    /// <summary>First number of the P&amp;L expense block both provisioning paths allocate from.</summary>
    private const int ExpenseAccountBlockStart = 62900100;

    // PGC group 6, the accounts Holded's expenses-accounts list holds.
    private const int ExpenseAccountsFrom = 60000000;
    private const int ExpenseAccountsTo = 69999999;
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
        var labels = (await ActiveYearCategoriesAsync(ct))
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

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var categories = (await ActiveYearCategoriesAsync(ct))
            .Select(c => (Id: c.Key, c.Value.Name, c.Value.Group))
            .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map.ToDictionary(m => m.BudgetCategoryId);

        // Avoid numbers in BOTH the local map and the chart mirror, so one taken remotely but missing locally (a failed
        // map write, or an account made directly in Holded) is never re-proposed.
        var remoteAccounts = await holded.GetChartAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.Number))
            .ToHashSet();

        var rows = new List<HoldedProvisioningRow>();
        // Seeded up front, not as the walk meets them: otherwise a ToAdd category sorting before a mapped one could be
        // handed its tag, and two rows sharing a tag make attribution arbitrary.
        var usedTags = map.Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
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
        // The plan proposes numbers the mirror lacks; a stale mirror would propose one made in Holded's UI since.
        await holded.SyncChartAsync(ct);
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
                var account = await holdedWrite.EnsureExpenseAccountAsync(
                    row.ProposedAccountNum!.Value, accountName, ct);
                var id = account.HoldedId;
                // Audited as soon as Holded holds the account: a failed map save below must not hide it.
                await audit.LogAsync(FinanceAuditActions.HoldedCategoryAccountProvisioned, HoldedExpenseAccount,
                    row.BudgetCategoryId,
                    account.Created
                        ? $"Provisioned Holded expense account {row.ProposedAccountNum.Value} '{accountName}'"
                        : $"Mapped existing Holded expense account {row.ProposedAccountNum.Value} for '{accountName}'",
                    actorUserId);
                await repo.AddCategoryMapAsync(new HoldedCategoryMap
                {
                    Id = Guid.NewGuid(),
                    BudgetCategoryId = row.BudgetCategoryId,
                    HoldedAccountNumber = row.ProposedAccountNum.Value,
                    HoldedAccountId = id,
                    Tag = row.Tag,
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

    public async Task<string?> GetHoldedAccountIdForCategoryAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        return map.FirstOrDefault(m => m.BudgetCategoryId == budgetCategoryId)?.HoldedAccountId;
    }

    /// <summary>Category and group names resolved from the active budget year.</summary>
    public async Task<IReadOnlyList<CategoryAccountRow>> GetCategoryMapAsync(CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var categories = await ActiveYearCategoriesAsync(ct);

        return map.Select(m => new CategoryAccountRow(
            m.BudgetCategoryId,
            categories.TryGetValue(m.BudgetCategoryId, out var c) ? c.Name : null,
            categories.TryGetValue(m.BudgetCategoryId, out var g) ? g.Group : null,
            m.HoldedAccountNumber,
            m.HoldedAccountId,
            m.Tag,
            m.UpdatedAt)).ToList();
    }

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

    /// <summary>Bounded, dash-free key; existing mapped tags always win collisions.</summary>
    private static string UniqueTag(
        string groupName, string categoryName, Guid categoryId,
        HashSet<string> usedTags)
    {
        var baseTag = HoldedMatcher.NormalizeTag(groupName + categoryName);
        if (baseTag.Length == 0) baseTag = categoryId.ToString("N");
        baseTag = baseTag[..Math.Min(baseTag.Length, HoldedCategoryMap.TagMaxLength)];
        if (!usedTags.Contains(baseTag)) return baseTag;

        var categorySuffix = categoryId.ToString("N")[..4];
        var suffix = categorySuffix;
        for (var attempt = 1; ; attempt++)
        {
            var candidate = baseTag[..Math.Min(baseTag.Length, HoldedCategoryMap.TagMaxLength - suffix.Length)] + suffix;
            if (!usedTags.Contains(candidate)) return candidate;
            suffix = categorySuffix + attempt.ToString(CultureInfo.InvariantCulture);
        }
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

    internal const string HoldedCreditorAccount = "HoldedCreditorAccount";

    // A new expense-only contact gets type "creditor", which Holded numbers in the 410 series rather than the
    // 400-series proveedor accounts older members carry, so the block spans both. Bindings, not the range, tell a
    // member's account from an org vendor's.
    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 41999999;

    internal static bool IsMemberCreditorAccount(int? accountNum) =>
        accountNum is >= CreditorAccountMin and <= CreditorAccountMax;

    public async Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var b = await MemberBindingAsync(userId, ct);
        return b is null ? null : ToBinding(b);
    }

    /// <summary>Every id the human has held, <paramref name="userId"/> included: their own ids never conflict.</summary>
    private async Task<IReadOnlyList<Guid>> MemberIdsAsync(Guid userId, CancellationToken ct) =>
        MemberIds(await users.GetUserInfoAsync(userId, ct), userId);

    private static IReadOnlyList<Guid> MemberIds(UserInfo? info, Guid userId) =>
        info?.AllUserIds is { } ids && ids.Contains(userId) ? ids : [userId];

    /// <summary><paramref name="userId"/>'s own binding, else the one on another of the human's ids: a SEPA row
    /// generated before a merge names the archived id, and a merge before <see cref="ReassignAsync"/> left the binding
    /// there.</summary>
    private async Task<HoldedCreditorContact?> MemberBindingAsync(Guid userId, CancellationToken ct)
    {
        if (await repo.GetCreditorContactByUserAsync(userId, ct) is { } own) return own;
        foreach (var id in await MemberIdsAsync(userId, ct))
            if (id != userId && await repo.GetCreditorContactByUserAsync(id, ct) is { } merged)
                return merged;
        return null;
    }

    /// <summary>Every binding by its own user id, plus each of <paramref name="userIds"/> that reaches one only
    /// through a merge. One snapshot, so a row is judged against a single read.</summary>
    private async Task<Dictionary<Guid, HoldedCreditorContact>> BindingByUserAsync(
        IEnumerable<Guid> userIds, CancellationToken ct)
    {
        // UserId is the one column the DB keeps unique, so this cannot throw.
        var byUser = (await repo.GetCreditorContactsAsync(ct)).ToDictionary(c => c.UserId);
        foreach (var id in userIds.Distinct().Where(id => !byUser.ContainsKey(id)).ToList())
            foreach (var other in await MemberIdsAsync(id, ct))
                if (byUser.TryGetValue(other, out var merged))
                {
                    byUser[id] = merged;
                    break;
                }
        return byUser;
    }

    /// <summary>The survivor takes the archived binding only when it has none. Two bindings are two Holded contacts,
    /// each with its own creditor ledger, so neither is dropped: the archived one stays for an admin to settle.</summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        if (await repo.MoveCreditorContactAsync(mergedFromUserId, mergedToUserId, now, ct) is { } moved)
        {
            await audit.LogAsync(FinanceAuditActions.HoldedCreditorBound, HoldedCreditorAccount, mergedToUserId,
                MovedBindingDescription(moved, mergedFromUserId), actorUserId, mergedToUserId, AuditSubjectTypes.User);
            return;
        }
        if (await repo.GetCreditorContactByUserAsync(mergedFromUserId, ct) is { } kept)
            logger.LogWarning(
                "Merging member {ArchivedId} into {SurvivorId} kept the archived creditor binding (Holded contact " +
                "{HoldedContactId} / account {SupplierAccountNum}) in place: the survivor has its own. Settle the " +
                "two accounts on /Finance/Creditors.",
                mergedFromUserId, mergedToUserId, kept.HoldedContactId, kept.SupplierAccountNum);
    }

    private static string MovedBindingDescription(HoldedCreditorContact moved, Guid fromUserId) =>
        $"Moved creditor account {moved.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "(unresolved)"} "
        + $"(Holded contact {moved.HoldedContactId}) from merged account {fromUserId}";

    internal static CreditorContactBinding ToBinding(HoldedCreditorContact b) =>
        new(b.UserId, b.HoldedContactId, b.SupplierAccountNum, b.Source) { CreatedAt = b.CreatedAt };

    // An account, and the Holded contact behind it, binds to at most one member. All three write paths check with
    // FindConflictingBinding and differ only in the remedy: refuse when the value is our guess, write and let the
    // collision show when it is Holded's own word. Not a unique DB index; see Docs/health.md.

    /// <summary>Overlap on either the 400000xx or the contact merges two members' payables, so both are conflicts.</summary>
    private static HoldedCreditorContact? FindConflictingBinding(
        IEnumerable<HoldedCreditorContact> bindings,
        IReadOnlyList<Guid> memberIds,
        int? supplierAccountNum,
        string? holdedContactId) =>
        bindings.FirstOrDefault(b =>
            !memberIds.Contains(b.UserId)
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
        var memberIds = await MemberIdsAsync(userId, ct);
        if (FindConflictingBinding(bindings, memberIds, supplierAccountNum, holdedContactId: null) is not null)
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
        if (FindConflictingBinding(bindings, memberIds, supplierAccountNum: null, contact.Id) is not null)
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
        var info = await users.GetUserInfoAsync(userId, ct);
        var memberIds = MemberIds(info, userId);
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        // A merge before ReassignAsync left the survivor's binding on an archived id: take it over as a merge now does,
        // rather than mint a second contact and creditor account for the same human.
        if (binding is null && info?.Id == userId)
            foreach (var mergedId in info.MergedUserIds)
                if (await repo.MoveCreditorContactAsync(mergedId, userId, clock.GetCurrentInstant(), ct) is { } moved)
                {
                    await audit.LogAsync(FinanceAuditActions.HoldedCreditorBound, HoldedCreditorAccount, userId,
                        MovedBindingDescription(moved, mergedId), jobName, userId, AuditSubjectTypes.User);
                    binding = moved;
                    break;
                }
        var allBindings = await repo.GetCreditorContactsAsync(ct);

        // The seed is our guess off a prior report, not Holded's word, so it gets the manual bind's remedy: refuse and
        // let the push mint a contact. That is also what makes Unbind durable.
        if (binding is null
            && FindConflictingBinding(allBindings, memberIds, seedAccountNum, seedContactId) is { } seedConflict)
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
        var conflict = FindConflictingBinding(allBindings, memberIds, accountNum, contactId);
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
            await repo.GetCreditorContactsAsync(ct), await MemberIdsAsync(userId, ct), supplierAccountNum,
            holdedContactId: null);
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

    internal async Task<HoldedCreditorContact?> DeleteCreditorBindingAsync(Guid userId, CancellationToken ct)
    {
        var removed = await repo.DeleteCreditorContactAsync(userId, ct);
        if (removed is not null)
            logger.LogInformation("Cleared the creditor binding for member {UserId}.", userId);
        return removed;
    }

    internal const string ContactListUnavailable =
        "The Holded contact mirror has never synced, so no member's creditor account is known. Run Sync now on "
        + "/Holded.";
    private DateTimeZone OrgZone => orgTimeZone.Zone;

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
                : payments.Max(l => l.Date).InZone(OrgZone).Date,
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
            var purchaseDocs = mirror.Docs;
            if (mirror.Docs.All(d => d.PaymentsPending is not null))
                openDocsByContact = OpenDocs(purchaseDocs)
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
            .Where(r => !r.IsBooked && !r.IsWithdrawn && !IsStale(r))
            .GroupBy(r => r.SupplierAccountNum)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.GeneratedAt)!.FileName);

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

    private async Task<IReadOnlyList<HoldedDuplicateMatcher.Match>> FindDuplicateDocumentsAsync(
        IReadOnlyList<HoldedPurchaseDocInfo> docs,
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

    internal const string AdditionalHoldedCreditorAccounts = "AdditionalHoldedCreditorAccounts";
    internal const string SepaPayouts = "SepaPayouts";
    internal const string CreditorLedger = "CreditorLedger";
    internal const string HoldedCreditorContacts = "HoldedCreditorContacts";

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

    internal const string PurchaseMirrorUnavailable =
        "The Holded purchase-document mirror has never synced, so no duplicate can be judged. Run Sync now on /Holded.";

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
        var docs = mirror.Docs;
        string? Refusal(HoldedPurchaseDocInfo d) => d.PaymentsPending is null
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
                      && d.Notes is { } notes
                      && string.Equals(notes, b.Notes, StringComparison.Ordinal));
        var deletableIds = verified.SelectMany(m => m.Duplicates.Select(d => d.Id)).ToHashSet(StringComparer.Ordinal);
        var byRef = (IEqualityComparer<ExpenseHoldedBooking>)ReferenceEqualityComparer.Instance;
        var candidateByBooking = candidates.ToDictionary(m => m.Booking, byRef);
        var verifiedByBooking = verified.ToDictionary(m => m.Booking, byRef);

        var matched = new List<(ExpenseHoldedBooking Booking, HoldedPurchaseDocInfo Kept, HoldedDuplicateLine Line)>();
        foreach (var linked in bookings)
        {
            var named = candidateByBooking.GetValueOrDefault(linked);
            var ok = verifiedByBooking.GetValueOrDefault(linked);
            if (named is null && ok is null) continue;
            var deletable = ok?.Duplicates.Select(d => d.Id).ToList() ?? [];
            var refused = (named?.Duplicates ?? [])
                .Where(d => !deletableIds.Contains(d.Id))
                .Select(d => new HoldedDuplicateRefusal(d.Id, d.Total, Refusal(d)
                    ?? (d.Notes is null
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
    private static string? DeleteRefusal(HoldedPurchaseDocInfo d)
    {
        if (d.PaymentsPending >= d.Total) return null;
        return !d.IsApproved
            ? "still a draft in Holded — delete it by hand"
            : $"has a payment on it ({Euros(d.Total - d.PaymentsPending.GetValueOrDefault())} of {Euros(d.Total)} paid) — settle it in Holded by hand";
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


    // Persisted audit entity type, shared with payout generation: never rename.
    private const string SepaTransferEntityType = nameof(SepaPayoutTransfer);

    /// <summary>The audit actor for the sweep.</summary>
    private const string SepaBookingJobName = "sepa-bank-booking";

    /// <summary>The line has no Guid, so its movement id rides in the description.</summary>
    private const string SepaBankLineEntityType = "SepaBankLine";

    /// <summary>Below half a cent is zero at two decimal places.</summary>
    private const decimal CentEpsilon = 0.005m;

    /// <summary>Chunk size once Holded refuses the whole list. One bank line can name 100+ payments, and the endpoint
    /// is untested above a handful, so the one-call form goes first.</summary>
    private const int ReconcileChunkSize = 50;

    /// <summary><c>BookedAt</c> is read before the Holded postings and written after, so without this two concurrent
    /// bookings of one transfer both post the whole amount.</summary>
    private static readonly SemaphoreSlim BookingGate = new(1, 1);

    /// <summary>Runs that may fail closed on the same undated pending line before the sweep skips it. Every two hours,
    /// that is about six hours before one stuck line stops blocking the whole feed.</summary>
    private const int StuckLineSkipAfterRuns = 3;

    /// <summary>How far back a file may have been generated and still match. The feed is read
    /// <see cref="GenerationSlackDays"/> further back (<see cref="FeedFrom"/>).</summary>
    internal const int FeedWindowDays = 90;

    /// <summary>The bank can value-date a day early, and the feed's dates are org-zone days.</summary>
    private const int GenerationSlackDays = IFinanceAdminService.DefaultSepaMatchToleranceDays;

    public async Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError, Instant? BankFeedSyncedAt)>
        GetSepaPayoutsAsync(int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays,
            CancellationToken ct = default)
    {
        var tolerance = ClampTolerance(toleranceDays);
        var mirror = await holded.GetBankMovementsAsync(
            sepa.Value.TreasuryAccountId, FeedFrom(tolerance), Today(), ct);
        var (rows, unavailable, unmatched, error) = await PairAsync(
            tolerance,
            () => mirror.LastSyncAt is null
                ? throw new HoldedTransientException("Holded's bank feed has not been synced yet.")
                : Task.FromResult(mirror.Movements),
            ct);
        return (rows, unavailable, unmatched, error, mirror.LastSyncAt);
    }

    /// <summary>The page's pairing over <paramref name="feed"/>: the page passes the mirror as it stands, a booking
    /// a freshly synced read. A feed that cannot be read costs the candidates and the panel, not the rows.</summary>
    private async Task<(IReadOnlyList<SepaPayoutTransferRow> Rows, string? UnavailableReason,
        IReadOnlyList<SepaBankMovementVm> UnmatchedMovements, string? BankFeedError)>
        PairAsync(int tolerance, Func<Task<IReadOnlyList<HoldedBankMovementDto>>> feed, CancellationToken ct)
    {
        var (withReasons, unavailable) = await GetSepaTransfersAsync(ct);
        if (withReasons.Count == 0 || unavailable is not null) return (withReasons, unavailable, [], null);
        // A withdrawn transfer renders on its row and nowhere else: no line can be its payment.
        var live = Live(withReasons);

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await feed();
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            // The page still lists everything it lists; it just cannot offer a line to book against.
            logger.LogWarning("The Sabadell bank feed could not be read for /Finance/Sepa. {Reason}", ex.Message);
            // Admin-only page: Holded's message names what failed, which is what the treasurer needs.
            return (withReasons, null, [],
                "The Sabadell bank feed could not be read, so no transfer can be booked right now: "
                + ex.Message);
        }

        var unmatched = new List<SepaBankMovementVm>();
        var candidateByTransfer = new Dictionary<Guid, (HoldedBankMovementDto Line, bool ByText)>();
        var considered = movements
            .Where(NeedsAMatch)
            // A line this section already booked is nobody's problem — it renders on its own row.
            .Where(m => !withReasons.Any(
                r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            .Select(m =>
            {
                var account = RemittanceAccountNum(m.Description);
                // Ambiguity is judged over every unbooked row, bookable or not, as PairingRefusal does, so a line the
                // page pairs is one the booking accepts. A textless line matches on amount and date alone, and never
                // while it could also be a whole file's debit.
                return (Movement: m, Account: account, Matches:
                    account is null && TextlessLineFitsAFile(live, m, tolerance)
                        ? []
                        : UnbookedMatches(live, account, Math.Abs(m.Amount), m.Date, tolerance));
            })
            .ToList();

        // A transfer two lines could each have paid is ambiguous too: neither books it and both go to the panel.
        // PairingRefusal's rival check counts the same lines.
        var claimants = considered
            .SelectMany(c => c.Matches.Select(r => r.TransferId))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // Read over every outgoing line, claimed or not: a batch a Process run got part-way through carries this line
        // on its booked rows and must stay processable.
        var batchLines = movements
            .Where(NeedsAMatch)
            .Select(m => (Movement: m, Files: BatchFiles(live, m, movements, tolerance)))
            .ToList();
        // Every line a file fits counts against it, ambiguous or not — as Process's rival check does.
        var batchClaimants = batchLines
            .SelectMany(b => b.Files)
            .GroupBy(f => f)
            .ToDictionary(g => g.Key, g => g.Count());
        var batchByFile = batchLines
            .Where(b => b.Files.Count == 1 && batchClaimants[b.Files[0]] == 1)
            .ToDictionary(b => b.Files[0], b => b.Movement);
        var batchFilesByLine = batchLines
            .GroupBy(b => b.Movement.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Files, StringComparer.Ordinal);

        foreach (var (m, account, matches) in considered)
        {
            if (matches.Count == 1 && claimants[matches[0].TransferId] == 1
                && matches[0].NotBookableReason is null)
            {
                candidateByTransfer[matches[0].TransferId] = (m, account is not null);
                continue;
            }

            // Equal amounts in one file, as many textless lines: paired one to one. One unbookable member keeps the
            // whole set on the panel, so Book file never books half.
            if (account is null && matches.Count > 1
                && TextlessSetPairs(live, movements, m, tolerance) is { Count: > 0 } set
                && set.All(p => p.Row.NotBookableReason is null))
            {
                candidateByTransfer[set.Single(p => string.Equals(p.Line.Id, m.Id, StringComparison.Ordinal)).Row.TransferId]
                    = (m, false);
                continue;
            }

            var files = batchFilesByLine.GetValueOrDefault(m.Id) ?? [];
            // It renders on its file's card, with the Process button.
            if (files.Count == 1 && batchByFile.TryGetValue(files[0], out var batch)
                && string.Equals(batch.Id, m.Id, StringComparison.Ordinal))
                continue;

            unmatched.Add(new SepaBankMovementVm(
                m.Id, m.Date, m.Amount, m.Description, account, m.Status,
                files.Count > 1
                    ? $"{files.Count} payout files total {Euros(Math.Abs(m.Amount))} — settle it in Holded by hand"
                    : files.Count == 1
                        ? "another bank line matches that file's total too — settle it in Holded by hand"
                        : account is null && TextlessLineFitsAFile(live, m, tolerance)
                            ? "its text names no creditor account, and it could be one transfer's line or "
                              + "a whole file's — settle it in Holded by hand"
                            : matches.Count == 0
                                ? account is null
                                    ? $"its text names no creditor account, and no unbooked transfer is for {Euros(Math.Abs(m.Amount))}"
                                    : $"no unbooked transfer of {Euros(Math.Abs(m.Amount))} on account {account}"
                                : matches.Count > 1
                                    ? account is null
                                        ? $"{matches.Count} unbooked transfers are for {Euros(Math.Abs(m.Amount))} and its text names no creditor account — book it by hand"
                                        : $"{matches.Count} unbooked transfers match it — settle it in Holded by hand"
                                    : matches[0].NotBookableReason is { } why
                                        ? $"the one transfer it matches (account {matches[0].SupplierAccountNum}) cannot be booked: {why}"
                                        : "another bank line matches that transfer too — settle it in Holded by hand"));
        }

        var batchVmByFile = batchByFile.ToDictionary(kv => kv.Key, kv => BatchLineVm(kv.Key, kv.Value));

        return (
            withReasons
                .Select(r => candidateByTransfer.TryGetValue(r.TransferId, out var c)
                    ? r with
                    {
                        CandidateBankMovementId = c.Line.Id,
                        CandidateBankMovementDate = c.Line.Date,
                        CandidateBankMovementAmount = c.Line.Amount,
                        CandidateBankMovementDescription = c.Line.Description,
                        CandidateMatchedByText = c.ByText,
                    }
                    : r)
                .Select(r => batchVmByFile.TryGetValue(r.FileId, out var batch) ? r with { BatchLine = batch } : r)
                .ToList(),
            null, unmatched, null);

        SepaBatchLineVm BatchLineVm(Guid fileId, HoldedBankMovementDto m) => new(
            m.Id, m.Date, m.Amount, m.Description,
            QuotesFileReference: (m.Description ?? "").Contains(
                fileId.ToString("N"), StringComparison.OrdinalIgnoreCase),
            NotProcessableReason: live
                .Where(r => r.FileId == fileId && !r.IsBooked && r.NotBookableReason is not null)
                .Select(r => $"the transfer on {r.SupplierAccountNum}: {r.NotBookableReason}")
                .FirstOrDefault());
    }

    /// <summary>The transfer booking state shared with <see cref="PairAsync"/>, without reading the bank feed.</summary>
    public async Task<(IReadOnlyList<SepaPayoutTransferRow> Transfers, string? UnavailableReason)>
        GetSepaTransfersAsync(CancellationToken ct = default)
    {
        var rows = await repo.GetSepaPayoutTransferRowsAsync(ct);
        var unavailable = BookingUnavailableReason();
        if (rows.Count == 0 || unavailable is not null) return (rows, unavailable);

        var bindingByUser = await BindingByUserAsync(rows.Select(r => r.UserId), ct);
        var withReasons = rows
            .Select(r => r with { NotBookableReason = NotBookableReason(r, bindingByUser) })
            .ToList();
        return (withReasons, null);
    }

    /// <summary>The row's reason on <c>/Finance/Sepa</c>, and the gate a batch Process checks before it posts
    /// anything.</summary>
    private string? NotBookableReason(
        SepaPayoutTransferRow row, IReadOnlyDictionary<Guid, HoldedCreditorContact> bindingByUser)
    {
        if (row.IsBooked) return null;
        if (row.IsWithdrawn) return null;   // renders as withdrawn; nothing books it
        // A payment Holded took without an id cannot be read back, so the resume cannot count it.
        if (row.PostedButNotBooked && HasUnconfirmedRef(row.HoldedPaymentRefs))
            return "an earlier attempt posted Holded payment(s) " + row.HoldedPaymentRefs
                   + " and could not identify one of them — settle it in Holded by hand";
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
        // Mirrors BookSepaTransferAsync's sibling-contact refusal so the button never renders only to fail. A null
        // contact id is a row from before the guard: account-only, as there.
        if (row.HoldedContactId is { Length: > 0 }
            && !string.Equals(row.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
            return "the member was rebound to a different Holded contact since this file was generated — book it by hand";
        return null;
    }

    public async Task RunSepaBookingSweepAsync(CancellationToken ct = default)
    {
        if (BookingUnavailableReason() is not null || !holded.IsConfigured) return;

        var rows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        var pending = rows.Where(r => r.ReconcilePending).ToList();
        var unbooked = rows.Where(r => r.BookedAt is null).ToList();
        // The sweep runs every two hours, so exactly one run a day lands in this band. That run reads the whole window
        // whatever is open, so /Finance's count of unreconciled Sabadell lines is at most a day behind.
        var nightRun = clock.GetCurrentInstant().InZone(OrgZone).Hour is 3 or 4;
        if (pending.Count == 0 && unbooked.Count == 0 && !nightRun) return;

        // Every reconcile-pending row's bank line must lie inside the window read. A pending row's lower bound is its
        // own first payable day with no floor, or a row aging out would never see a later reconcile. Both bounds key off
        // GeneratedAt, never BookedAt: a booking can land days after the line it waits on.
        var floor = FeedFrom(GenerationSlackDays);
        var matchFrom = unbooked.Select(FirstPayableDate).DefaultIfEmpty(floor).Min();
        if (matchFrom < floor) matchFrom = floor;
        var pendingFrom = pending.Select(FirstPayableDate).DefaultIfEmpty(matchFrom).Min();
        var from = pendingFrom < matchFrom ? pendingFrom : matchFrom;
        if (nightRun && floor < from) from = floor;

        IReadOnlyList<HoldedBankMovementDto> movements;
        try
        {
            movements = await ReadSweepFeedAsync(from, ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("The SEPA sweep could not read the Sabadell bank feed; nothing was booked. {Reason}", ex.Message);
            return;
        }

        var byId = movements.ToLookup(m => m.Id, StringComparer.Ordinal);

        // A pending row is re-reconciled from its recorded payment ids; rows of one Processed file go together. A line
        // Holded already reads reconciled is stamped as is. A `partial` line is not re-issued (the rest is a human's),
        // nor is a file a Process run got part-way through: linking it would make the line `partial`.
        foreach (var group in pending.GroupBy(r => r.HoldedBankMovementId!, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            // Finish the bank-line reconcile and every transfer stamp once this unit starts.
            var completionCt = CancellationToken.None;
            try
            {
                var line = byId[group.Key].FirstOrDefault();
                if (line is null) continue;
                Instant? at = IsReconciled(line) ? clock.GetCurrentInstant() : null;
                var fileStillOpen = rows.Any(r => !r.IsBooked && group.Any(g => g.FileId == r.FileId));
                if (at is null && IsUntouched(line) && !fileStillOpen)
                {
                    var refs = group.SelectMany(r => PaymentRefs(r.HoldedPaymentRefs)).ToList();
                    if (refs.Count > 0)
                    {
                        logger.LogInformation(
                            "SEPA sweep: re-issuing the reconcile of Sabadell line {MovementId} against "
                            + "{Count} payment(s) of {Rows} transfer(s).", line.Id, refs.Count, group.Count());
                        at = await TryReconcileAsync(line.Id, line.Date, refs, incomplete: true, completionCt);
                    }
                }
                if (at is null) continue;

                foreach (var row in group)
                {
                    await repo.MarkSepaTransferReconciledAsync(row.TransferId, at.Value, completionCt);
                    await audit.LogAsync(
                        FinanceAuditActions.SepaPayoutTransferBooked, SepaTransferEntityType, row.TransferId,
                        $"RECONCILED the Sabadell line {row.HoldedBankMovementId} that booked the SEPA payout "
                        + $"{Euros(row.Amount)} to {row.IbanMasked} on creditor account {row.SupplierAccountNum}.",
                        SepaBookingJobName, row.UserId, AuditSubjectTypes.User);
                }
            }
            catch (OperationCanceledException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        foreach (var m in movements.Where(NeedsAMatch))
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Any(r => string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                continue;

            // The sweep books only a line whose text names the account; textless ones wait for a human on the page.
            var account = RemittanceAccountNum(m.Description);
            if (account is null) continue;

            var matches = UnbookedMatches(unbooked, account.Value, Math.Abs(m.Amount), m.Date, GenerationSlackDays);
            if (matches.Count != 1)
            {
                // Surfaced on the page's panel; the sweep only logs it once per run.
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
            catch (OperationCanceledException ex)
            {
                // Booking already uses None; its dependency failures are not job shutdown.
                throw new InvalidOperationException(ex.Message, ex);
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

    /// <summary>An undated pending outgoing line fails the read closed (it could be a SEPA rival) for
    /// <see cref="StuckLineSkipAfterRuns"/> − 1 runs. The run that reaches the threshold skips it, reads the rest and
    /// audits once; later runs skip it with a warning until it reads again.</summary>
    private async Task<IReadOnlyList<HoldedBankMovementDto>> ReadSweepFeedAsync(
        LocalDate from, CancellationToken ct)
    {
        var previous = bookingState.TryGet(CacheKeys.SepaStuckBankLines, out var runsBefore) ? runsBefore : [];
        var thisRun = new Dictionary<string, int>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            try
            {
                var movements = await ReadBankFeedAsync(from, ct, skip);
                RememberStuckLines(thisRun);
                return movements;
            }
            catch (HoldedPermanentException ex) when (ex.BankMovementId is { } id && !skip.Contains(id))
            {
                var runs = previous.GetValueOrDefault(id) + 1;
                thisRun[id] = runs;
                if (runs < StuckLineSkipAfterRuns)
                {
                    RememberStuckLines(thisRun);
                    throw;
                }

                skip.Add(id);
                RememberStuckLines(thisRun);
                logger.LogWarning(
                    "SEPA sweep: skipping Sabadell line {MovementId}, pending outgoing with no readable date "
                    + "for {Runs} consecutive runs; the rest of the feed is booked without it.", id, runs);
                if (runs == StuckLineSkipAfterRuns)
                    await audit.LogAsync(
                        FinanceAuditActions.SepaBankLineSkipped, SepaBankLineEntityType, Guid.Empty,
                        $"The SEPA sweep skipped Sabadell line {id}: it has been pending outgoing with no "
                        + $"readable date for {runs} consecutive runs, and blocked every booking until now. "
                        + "The rest of the feed books without it, so it can no longer count as a rival of a "
                        + "transfer's amount — fix or reconcile it in Holded.",
                        SepaBookingJobName);
            }
        }
    }

    /// <summary>
    /// In the singleton tracked cache because the service is scoped and the count must outlive one run. A restart
    /// only restarts the count, the safe direction. A line absent from a run drops out.
    /// </summary>
    private void RememberStuckLines(Dictionary<string, int> runs)
    {
        if (runs.Count == 0) bookingState.Invalidate(CacheKeys.SepaStuckBankLines);
        else bookingState.Set(CacheKeys.SepaStuckBankLines, runs);
    }

    public Task<SepaBookingResult> BookSepaTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookOneTransferAsync(
            transferId, bankMovementId, actorUserId, ClampTolerance(toleranceDays)));

    public Task<SepaBookingResult> BookSepaFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookOneFileAsync(fileId, bankMovementId, actorUserId, ClampTolerance(toleranceDays)));

    public Task<SepaBookingResult> BookSepaFileMatchesAsync(
        Guid fileId, Guid actorUserId,
        int toleranceDays = IFinanceAdminService.DefaultSepaMatchToleranceDays) =>
        RunBookingAsync(() => BookFileMatchesAsync(fileId, actorUserId, ClampTolerance(toleranceDays)));

    /// <summary>Never negative, never past the feed window: beyond it there is no line to match.</summary>
    private static int ClampTolerance(int days) => Math.Clamp(days, 0, FeedWindowDays);

    /// <summary>All-or-nothing: a file whose rows are not all matched is not approved, so nobody books half a file.
    /// Each row then re-validates its own line; the first refusal stops the run and booked rows stay booked.</summary>
    private async Task<SepaBookingResult> BookFileMatchesAsync(Guid fileId, Guid actorUserId, int tolerance)
    {
        var (rows, unavailable, _, feedError) = await PairAsync(
            tolerance, () => ReadBankFeedAsync(FeedFrom(tolerance), CancellationToken.None), CancellationToken.None);
        if (unavailable is not null) return new SepaBookingResult(false, unavailable);
        if (feedError is not null) return new SepaBookingResult(false, feedError + " Nothing was posted.");

        var file = rows.Where(r => r.FileId == fileId).ToList();
        if (file.Count == 0)
            return new SepaBookingResult(false, "That payout file no longer exists — reload the page.");
        // A withdrawn transfer is not the file's to book: the gate is over the rest.
        var unbooked = Live(file).Where(r => !r.IsBooked).OrderBy(r => r.SupplierAccountNum).ToList();
        if (unbooked.Count == 0)
            return new SepaBookingResult(false, "Every transfer in that file is already booked or withdrawn — nothing was posted.");
        var unmatched = unbooked.Where(r => !r.CanBook).ToList();
        if (unmatched.Count > 0)
            return new SepaBookingResult(false,
                $"{unmatched.Count} of the file's {unbooked.Count} unbooked transfers have no Sabadell line "
                + $"(creditor account{(unmatched.Count == 1 ? "" : "s")} "
                + string.Join(", ", unmatched.Select(r => r.SupplierAccountNum.ToString(CultureInfo.InvariantCulture)))
                + ") — the file is approved only when every transfer in it is matched. Nothing was posted.");

        var booked = 0;
        foreach (var row in unbooked)
        {
            var result = await BookOneTransferAsync(row.TransferId, row.CandidateBankMovementId!, actorUserId, tolerance);
            if (!result.Succeeded)
                return new SepaBookingResult(false,
                    $"{booked} of {unbooked.Count} transfers booked; the one on creditor account "
                    + $"{row.SupplierAccountNum} was not: {result.Message} Approve the file again to "
                    + "book the rest.");
            booked++;
        }

        return new SepaBookingResult(true,
            $"Booked all {booked} transfer{(booked == 1 ? "" : "s")} of {file[0].FileName} against "
            + "their Sabadell lines.");
    }

    /// <summary>The row is kept and flagged; nothing pairs, books or counts it again. Only an unbooked row with no
    /// Holded posting can be withdrawn.</summary>
    public Task<SepaBookingResult> WithdrawSepaTransferAsync(
        Guid transferId, Guid actorUserId, CancellationToken ct = default) =>
        UnderBookingGateAsync(() => WithdrawSepaTransferGatedAsync(transferId, actorUserId, ct));

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

        await SendWithdrawnEmailAsync(transfer, FileDate(row));

        return new SepaBookingResult(true,
            $"Withdrew the {Euros(transfer.Amount)} transfer to {transfer.IbanMasked} from {row.FileName}; "
            + "the member has been told and the account is payable again.");
    }

    /// <summary>For a file the bank rejected whole: withdraws every transfer still live in it, in one save, so it is
    /// cancelled. All or nothing: refused if any live transfer could not be withdrawn on its own. No member email,
    /// unlike a single withdrawal: the file is usually reissued at once, and that sends the payout email again.</summary>
    public Task<SepaBookingResult> CancelSepaFileAsync(Guid fileId, Guid actorUserId, CancellationToken ct = default) =>
        UnderBookingGateAsync(() => CancelSepaFileGatedAsync(fileId, actorUserId, ct));

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

    private async Task<SepaBookingResult> RunBookingAsync(Func<Task<SepaBookingResult>> booking)
    {
        if (BookingUnavailableReason() is { } unavailable)
            return new SepaBookingResult(false, unavailable);

        // The "already booked?" read and the stamp sit either side of several Holded round-trips, so two callers (a Book
        // click during the sweep, a double submit) would both post the whole amount. Serialize both entry points.
        return await UnderBookingGateAsync(booking);
    }

    /// <summary>For a write that must not interleave with a booking, such as a withdrawal: otherwise a row checked as
    /// unbooked could be withdrawn while a booking posts its payments, and end up booked and withdrawn at once.</summary>
    private async Task<T> UnderBookingGateAsync<T>(Func<Task<T>> work)
    {
        await BookingGate.WaitAsync(CancellationToken.None);
        try
        {
            return await work();
        }
        finally
        {
            BookingGate.Release();
        }
    }

    /// <summary>The postings to reconcile the file's shared line against, once, after the last transfer.</summary>
    private sealed class BatchBooking(HoldedBankMovementDto movement, int transferCount)
    {
        public HoldedBankMovementDto Movement { get; } = movement;

        public int TransferCount { get; } = transferCount;

        public List<HoldedReconcileDocumentRef> Docs { get; } = [];

        /// <summary>Some of the line's money cannot be named to Holded, so an accepted reconcile may still leave it
        /// <c>partial</c>.</summary>
        public bool Incomplete { get; set; }
    }

    private async Task<SepaBookingResult> BookOneFileAsync(
        Guid fileId, string bankMovementId, Guid actorUserId, int tolerance)
    {
        // Same rule as a single booking: once one transfer is posted, the rest have to follow.
        var ct = CancellationToken.None;

        // Withdrawn transfers left the file before the bank debited it: the total the line has to
        // match is the rest.
        var rows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        var file = rows.Where(r => r.FileId == fileId).ToList();
        if (file.Count == 0)
            return new SepaBookingResult(false, "That payout file no longer exists — reload the page.");

        IReadOnlyList<HoldedBankMovementDto> feed;
        try
        {
            feed = await ReadBankFeedAsync(FeedFrom(tolerance), ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not read the Sabadell bank feed to process SEPA file {FileId}.", fileId);
            return new SepaBookingResult(false, "The Sabadell bank feed could not be read — nothing was posted.");
        }

        var movement = feed.FirstOrDefault(x => string.Equals(x.Id, bankMovementId, StringComparison.Ordinal));
        if (movement is null)
            return new SepaBookingResult(false,
                $"That Sabadell line is no longer in the last {FeedWindowDays} days of the feed — "
                + "reload /Finance/Sepa.");

        // The pairing, re-validated: the posted ids are never trusted.
        if (BatchLineRefusal(file, movement, rows, feed, tolerance) is { } refusal)
            return new SepaBookingResult(false, refusal);
        var files = BatchFiles(rows, movement, feed, tolerance);
        if (files.Count > 1)
            return new SepaBookingResult(false,
                $"{files.Count} payout files total {Euros(Math.Abs(movement.Amount))} — Humans cannot tell "
                + "which one that Sabadell line paid; settle it in Holded by hand. Nothing was posted.");
        var rivals = feed.Count(x => !string.Equals(x.Id, movement.Id, StringComparison.Ordinal)
                                     && NeedsAMatch(x) && BatchLineRefusal(file, x, rows, feed, tolerance) is null);
        if (rivals > 0)
            return new SepaBookingResult(false,
                $"{rivals + 1} Sabadell lines could each be this file's debit — Humans cannot tell which "
                + "one is; settle it in Holded by hand. Nothing was posted.");

        // Every transfer is checked before any is posted, so a bad binding refuses the whole file
        // rather than stopping it half-way.
        var bindingByUser = await BindingByUserAsync(file.Select(r => r.UserId), ct);
        var unbooked = file.Where(r => !r.IsBooked).OrderBy(r => r.SupplierAccountNum).ToList();
        foreach (var row in unbooked)
            if (NotBookableReason(row, bindingByUser) is { } why)
                return new SepaBookingResult(false,
                    $"The transfer on {row.SupplierAccountNum} cannot be booked: {why}. Nothing was posted.");

        var batch = new BatchBooking(movement, file.Count);
        var booked = 0;
        foreach (var row in unbooked)
        {
            var result = await BookOneTransferAsync(row.TransferId, movement.Id, actorUserId, tolerance, batch);
            if (!result.Succeeded)
                return new SepaBookingResult(false,
                    $"{booked} of {unbooked.Count} transfer(s) booked, then the one on "
                    + $"{row.SupplierAccountNum} was not: {result.Message} Process the file again to "
                    + "finish — booked transfers are skipped.");
            booked++;
        }

        // One reconcile for the whole line. A file an earlier run got part-way through cannot name that run's
        // postings, so only Holded then saying "reconciled" stamps it.
        var resumed = unbooked.Count < file.Count;
        var reconciledAt = await TryReconcileAsync(
            movement.Id, movement.Date, batch.Docs, batch.Incomplete || resumed, ct);
        if (reconciledAt is not null)
            foreach (var row in file)
            {
                await repo.MarkSepaTransferReconciledAsync(row.TransferId, reconciledAt.Value, ct);
                await audit.LogAsync(
                    FinanceAuditActions.SepaPayoutTransferBooked, SepaTransferEntityType, row.TransferId,
                    $"RECONCILED the Sabadell line {movement.Id} that paid the whole file "
                    + $"{row.FileName}, including this transfer of {Euros(row.Amount)} to "
                    + $"{row.IbanMasked} on creditor account {row.SupplierAccountNum}.",
                    actorUserId, row.UserId, AuditSubjectTypes.User);
            }

        return new SepaBookingResult(true,
            $"Booked {booked} transfer(s) of {file[0].FileName} against Sabadell line {movement.Id} "
            + $"({Euros(movement.Amount)}); "
            + (reconciledAt is not null
                ? "the line is reconciled."
                : "RECONCILE PENDING — reconcile the line in Holded by hand; the sweep re-issues it meanwhile."));
    }

    private async Task<SepaBookingResult> BookOneTransferAsync(
        Guid transferId, string bankMovementId, Guid? actorUserId, int tolerance, BatchBooking? batch = null)
    {
        // No request token at all: once a payment is posted to Holded the rest of the allocation has to finish.
        var ct = CancellationToken.None;

        var transfer = await repo.GetSepaTransferAsync(transferId, ct);
        if (transfer is null)
            return new SepaBookingResult(false, "That transfer no longer exists — reload the page.");

        // Idempotency is the row itself: a second POST finds BookedAt set and pays nothing.
        if (transfer.BookedAt is not null)
            return new SepaBookingResult(false,
                $"Transfer to {transfer.IbanMasked} is already booked — nothing was posted to Holded.");

        // Cancelled at the bank: no line is this transfer's, whatever the amount says.
        if (transfer.WithdrawnAt is not null)
            return new SepaBookingResult(false,
                $"Transfer to {transfer.IbanMasked} was withdrawn — nothing was posted to Holded.");

        // An earlier run posted payments and stopped. Holded drops the description on a purchase payment, so the tag
        // sum cannot see them: the resume reads each saved id back off the member's documents instead. An id-less
        // payment cannot be read back.
        var priorRefs = SplitRefs(transfer.HoldedPaymentRefs);
        if (HasUnconfirmedRef(transfer.HoldedPaymentRefs))
            return new SepaBookingResult(false,
                $"An earlier attempt posted Holded payment(s) {transfer.HoldedPaymentRefs} for this "
                + "transfer and could not identify one of them, so Humans cannot tell what is still "
                + "owed. Settle it in Holded by hand; nothing was posted.");

        var binding = await MemberBindingAsync(transfer.UserId, ct);
        if (binding is null || string.IsNullOrEmpty(binding.HoldedContactId))
            return new SepaBookingResult(false,
                "The member paid by that transfer has no Holded contact binding — bind them on "
                + "/Finance/Creditors first.");

        // The binding is the member's current one; a rebind since the file was built would pay a different account than
        // the bank statement quotes, which is unreconcilable.
        if (binding.SupplierAccountNum != transfer.SupplierAccountNum)
            return new SepaBookingResult(false,
                $"The member's Holded binding changed since this file was generated (now "
                + $"{binding.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "unresolved"}, "
                + $"the transfer pays {transfer.SupplierAccountNum}) — book it by hand.");

        // Holded lets two contacts share one 400000xx, so a rebind to a sibling contact slips past the check above. A
        // null contact id is a row from before this guard: account-only.
        if (transfer.HoldedContactId is { Length: > 0 }
            && !string.Equals(transfer.HoldedContactId, binding.HoldedContactId, StringComparison.Ordinal))
            return new SepaBookingResult(false,
                $"The member was rebound to a different Holded contact since this file was generated "
                + $"(now {binding.HoldedContactId}, the transfer paid {transfer.HoldedContactId}) — "
                + "book it by hand.");

        // Step 1: the bank line is the trigger, and its date is every posting's date. A batch has already read it.
        HoldedBankMovementDto? movement = batch?.Movement;
        IReadOnlyList<HoldedBankMovementDto> feed = [];
        if (batch is null)
        {
            try
            {
                feed = await ReadBankFeedAsync(FeedFrom(tolerance), ct);
                movement = feed.FirstOrDefault(
                    x => string.Equals(x.Id, bankMovementId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                logger.LogError(ex, "Could not read the Sabadell bank feed to book SEPA transfer {TransferId}.", transferId);
                return new SepaBookingResult(false,
                    "The Sabadell bank feed could not be read — nothing was posted.");
            }
        }

        if (movement is null)
            return new SepaBookingResult(false,
                $"That Sabadell line is no longer in the last {FeedWindowDays} days of the feed — "
                + "reload /Finance/Sepa.");

        // Step 2: re-validate the pairing. The posted movement id is never trusted.
        var allRows = Live(await repo.GetSepaPayoutTransferRowsAsync(ct));
        if (batch is null && PairingRefusal(movement, feed, allRows) is { } pairing)
            return new SepaBookingResult(false, pairing);

        // On every posting, so a Holded line traces back to one transfer and a retry can sum what was posted.
        var tag = "SEPA payout E" + transfer.Id.ToString("N");

        // Steps 3 and 4: the creditor balance, and what this transfer already posted, from the account's ledger
        // synced just now. Never the unsynced mirror: a stale sum here re-posts money that already left.
        decimal owedNow;
        decimal posted;
        var priorPaid = 0m;
        var refsSaved = true;
        try
        {
            if (!await holded.SyncAccountLedgerAsync(transfer.SupplierAccountNum, ct))
                return new SepaBookingResult(false,
                    "A Holded ledger sync is running, so this account cannot be checked — try again in a minute. "
                    + "Nothing was posted.");
            var lines = (await holded.GetLedgerLinesAsync(transfer.SupplierAccountNum, ct))
                .Where(l => l.AccountNum == transfer.SupplierAccountNum)
                .ToList();
            owedNow = lines.Sum(l => l.Credit - l.Debit);
            posted = lines
                .Where(l => (l.Description ?? "").Contains(tag, StringComparison.Ordinal))
                .Sum(l => l.Debit - l.Credit);   // paying a creditor DEBITS 400xxxxx
            if (posted < 0m) posted = 0m;        // a tagged credit is not "already paid"

            // Every booking, not only a resume: a run that died before saving its ids left payments nothing points at.
            if (PriorPaymentsRefusal(lines, movement.Date, transfer.SupplierAccountNum) is { } refusal)
                return new SepaBookingResult(false, refusal + " Nothing was posted.");
            posted += priorPaid;
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Could not sync Holded's creditor ledger to book SEPA transfer {TransferId}.", transferId);
            return new SepaBookingResult(false,
                "Holded's creditor ledger could not be synced — nothing was posted.");
        }

        // Step 5: the whole transfer less what is posted, never capped by what the account owes now: the money left,
        // so the ledger must show it (an overpaid account shows negative, and the audit names it). Rounded to cents
        // once, so per-document rounding cannot drift the total.
        var toPost = Math.Round(transfer.Amount - posted, 2, MidpointRounding.AwayFromZero);
        var resumed = posted > CentEpsilon;
        var overpaidBy = Math.Round(toPost - owedNow, 2, MidpointRounding.AwayFromZero);

        var paidDocs = new List<(string DocId, decimal Amount, string Ref)>();
        HoldedJournalEntryOutcome? entry = null;
        var entryAmount = 0m;
        var remaining = toPost;

        if (remaining > CentEpsilon)
        {
            IReadOnlyList<HoldedPurchaseDocInfo> open;
            try
            {
                // Synced just now: PaymentsPending is what keeps a paid document from being paid again.
                if (!await holded.SyncPurchaseDocumentsAsync(ct))
                    return new SepaBookingResult(false,
                        "A Holded purchase-document sync is running — try again in a minute. Nothing was posted.");
                open = OpenDocs((await holded.GetPurchaseDocumentsAsync(ct)).Docs
                        .Where(d => string.Equals(d.ContactId, binding.HoldedContactId, StringComparison.Ordinal))
                        .ToList())
                    // Oldest first: the money the member has been owed longest clears first.
                    .OrderBy(d => d.Date)
                    .ThenBy(d => d.Id, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
            {
                logger.LogError(ex, "Could not sync Holded purchase documents to book SEPA transfer {TransferId}.", transferId);
                return new SepaBookingResult(false, "Holded's purchase documents could not be synced — nothing was posted.");
            }

            // Step 6: FIFO document payments dated the bank line. PaymentsPending is read live, so a document already paid
            // shows 0 and is skipped: that makes this pass idempotent.
            foreach (var doc in open)
            {
                if (remaining <= CentEpsilon) break;
                var amount = Math.Round(
                    Math.Min(remaining, doc.PaymentsPending.GetValueOrDefault()), 2, MidpointRounding.AwayFromZero);
                if (amount <= 0m) continue;

                try
                {
                    paidDocs.Add((doc.Id, amount, await holdedWrite.PayPurchaseDocumentAsync(
                        doc.Id, amount, sepa.Value.TreasuryAccountId, movement.Date, tag, ct)));
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    return await RefusedMidBookingAsync(ex, "payment", "Holded document " + doc.Id);
                }

                remaining -= amount;
            }

            // Step 7: whatever the documents did not cover, as one journal entry: debit 400xxxxx, credit the bank.
            if (remaining > CentEpsilon)
            {
                try
                {
                    entryAmount = remaining;
                    entry = await holdedWrite.PostJournalEntryAsync(
                        movement.Date, transfer.SupplierAccountNum, sepa.Value.TreasuryLedgerAccount!.Value,
                        remaining, tag, ct);
                    remaining -= entryAmount;
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    entryAmount = 0m;
                    return await RefusedMidBookingAsync(
                        ex, "journal entry", "creditor account " + transfer.SupplierAccountNum);
                }
            }
        }

        // Step 8: persist, then reconcile, then stamp. Never the other order: losing the local save after Holded took
        // the money is the failure this design prevents.
        var now = clock.GetCurrentInstant();
        // The journal remainder rides along as an `entry:<amount>` token: not a Holded id, never
        // sent in a reconcile, but what lets the row say how much of the line a human has to match.
        var refTokens = priorRefs.Concat(paidDocs.Select(p => p.Ref)).ToList();
        if (entry is not null)
            refTokens.Add(SepaPayoutTransferRow.JournalEntryRefPrefix
                          + entryAmount.ToString("F2", CultureInfo.InvariantCulture));
        var refs = refTokens.Count == 0 ? null : string.Join(",", refTokens);
        try
        {
            await repo.SaveSepaTransferBookingAsync(transfer.Id, now, actorUserId, movement.Id, null, refs, ct);
        }
        catch (Exception ex)
        {
            // Money moved and the row does not say so: the one state that must never be invisible. Audit, then leave
            // it retryable.
            logger.LogError(ex,
                "Holded accepted every posting for SEPA transfer {TransferId} but the booking row could not be saved.",
                transferId);
            await RememberLandedPaymentsAsync();
            await AuditBookingAsync(
                $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                + $"{movement.Id}: Holded accepted {PostingSummary()} and then the transfer row "
                + $"could not be saved. The transfer is NOT booked; {NextStep()}.");
            return new SepaBookingResult(false,
                "Holded accepted the postings but the transfer row could not be saved — it is NOT "
                + $"marked booked; {NextStep()}.");
        }

        // Booking released the "in a payout file" gate, so the mirror is now all that says what the member is owed:
        // without this the account reads owed and payable until the nightly ledger sync.
        try
        {
            if (!await holded.SyncAccountLedgerAsync(transfer.SupplierAccountNum, CancellationToken.None))
                logger.LogWarning(
                    "SEPA transfer {TransferId} is booked, but a ledger sync was running: creditor account {Account} shows its old balance until the next sync.",
                    transferId, transfer.SupplierAccountNum);
        }
        catch (Exception ex) // anything: the money step is done, and what follows audits it
        {
            logger.LogWarning(ex,
                "SEPA transfer {TransferId} is booked, but creditor account {Account} could not be re-synced: it shows its old balance until the next sync.",
                transferId, transfer.SupplierAccountNum);
        }

        // Name the PAYMENTS, never the documents: a purchase ref makes Holded mint a new payment for the whole line.
        // An unconfirmed payment or a journal entry cannot be named, so Holded may read the line `partial`.
        var docs = PaymentRefs(refs);
        var incomplete = docs.Count < priorRefs.Count + paidDocs.Count || entry is not null || resumed;

        var summary = PostingSummary();
        var resumedPart = resumed ? $"; resumed ({Euros(posted)} already posted)" : "";
        // Named here: nothing refused it, and the balance alone will not say which transfer overpaid.
        var overpaidPart = toPost > CentEpsilon && overpaidBy > CentEpsilon
            ? $"; OVERPAID — the account owed {Euros(Math.Max(0m, owedNow))}, so this leaves it "
              + $"{Euros(overpaidBy)} in credit"
            : "";

        // The line is shared with the file: the batch reconciles it once, after the last transfer.
        if (batch is not null)
        {
            batch.Docs.AddRange(docs);
            if (incomplete) batch.Incomplete = true;
            await AuditBookingAsync(
                $"Booked SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
                + $"{transfer.SupplierAccountNum} against Sabadell line {movement.Id} dated "
                + $"{movement.Date.ToInvariantDate()}, one of {batch.TransferCount} transfers the bank "
                + $"debited as that one line ({Euros(movement.Amount)}): {summary}{resumedPart}"
                + $"{overpaidPart}; the line is reconciled once the whole file is booked.");
            return new SepaBookingResult(true, $"Booked {Euros(transfer.Amount)}: {summary}.");
        }

        var reconciledAt = await TryReconcileAsync(movement.Id, movement.Date, docs, incomplete, ct);
        if (reconciledAt is not null)
            await repo.MarkSepaTransferReconciledAsync(transfer.Id, reconciledAt.Value, ct);

        // Audit payment references and the journal outcome after saving the booking.
        var reconcilePart = reconciledAt is not null
            ? "reconciled."
            : entry is not null
                ? "RECONCILE PENDING — the journal entry cannot be linked to the bank line by API; "
                  + "reconcile the line in Holded by hand."
                : "RECONCILE PENDING — tick it in Holded.";
        var description =
            $"Booked SEPA payout {Euros(transfer.Amount)} to {transfer.IbanMasked} on creditor account "
            + $"{transfer.SupplierAccountNum} against Sabadell line {movement.Id} dated "
            + $"{movement.Date.ToInvariantDate()}: {summary}"
            + $"{resumedPart}{overpaidPart}; {reconcilePart}";

        await AuditBookingAsync(description);

        return new SepaBookingResult(true, $"Booked {Euros(transfer.Amount)}: {summary}.");

        Task AuditBookingAsync(string text) =>
            actorUserId is { } actor
                ? audit.LogAsync(
                    FinanceAuditActions.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, actor, transfer.UserId, AuditSubjectTypes.User)
                : audit.LogAsync(
                    FinanceAuditActions.SepaPayoutTransferBooked, SepaTransferEntityType, transfer.Id,
                    text, SepaBookingJobName, transfer.UserId, AuditSubjectTypes.User);

        string PostingSummary()
        {
            var docPart = paidDocs.Count == 0
                ? "0 document payment(s)"
                : $"{paidDocs.Count} document payment(s) ("
                  + string.Join(", ", paidDocs.Select(p => $"{p.DocId} {Euros(p.Amount)} {p.Ref}")) + ")";
            var entryPart = entry is null
                ? ""
                : $" and a journal entry for {Euros(entryAmount)} ({(entry.Confirmed ? "confirmed" : "unconfirmed")})";
            return docPart + entryPart;
        }

        // What Holded accepted is kept, but the row is not written: the next attempt re-reads the tag sum and posts
        // only the difference, so a partial booking is never terminal.
        async Task<SepaBookingResult> RefusedMidBookingAsync(Exception ex, string what, string where)
        {
            var landed = PostingSummary();
            if (paidDocs.Count > 0)
            {
                await RememberLandedPaymentsAsync();
                // Postings an automation caused, so they get an audit row on this path too — same
                // action as a completed booking, labelled PARTIAL.
                var partial =
                    $"PARTIAL SEPA booking of {Euros(transfer.Amount)} to {transfer.IbanMasked} on "
                    + $"creditor account {transfer.SupplierAccountNum} against Sabadell line "
                    + $"{movement.Id}: Holded accepted {landed} and then refused the {what}. The "
                    + $"transfer is NOT booked; {NextStep()}.";
                await AuditBookingAsync(partial);
            }

            logger.LogError(ex,
                "Booking SEPA transfer {TransferId} failed after {Posted} posting(s), on the {What} for {Where}.",
                transferId, paidDocs.Count, what, where);

            if (paidDocs.Count > 0)
                return new SepaBookingResult(false,
                    $"Holded accepted {paidDocs.Count} posting(s) and then refused the {what}; the "
                    + $"transfer is NOT marked booked; {NextStep()}.");

            // An accepted-but-unreadable posting returns an "unconfirmed:" ref, not an exception, so this is a real
            // refusal.
            return new SepaBookingResult(false, $"Holded refused the {what} — nothing was posted.");
        }

        // A journal entry keeps its tag and a saved payment id is read back, so a retry resumes from both. A payment
        // with no id cannot be read back.
        // Without the saved ids the retry cannot see the payments and would post them again.
        string NextStep() => !refsSaved
            ? "settle it in Holded by hand — the payment ids could not be saved, so a retry would pay again"
            : paidDocs.Any(p => HasUnconfirmedRef(p.Ref))
                ? "settle it in Holded by hand — Holded took a payment Humans could not identify"
                : "book it again to finish — the retry posts only what is still missing";

        // Written to the row, after any an earlier run saved, so the next attempt can read them back: the tag sum
        // cannot see a purchase payment.
        async Task RememberLandedPaymentsAsync()
        {
            if (paidDocs.Count == 0) return;
            try
            {
                await repo.SaveSepaTransferPaymentRefsAsync(
                    transfer.Id, string.Join(",", priorRefs.Concat(paidDocs.Select(p => p.Ref))), ct);
            }
            catch (Exception ex)
            {
                refsSaved = false;
                logger.LogError(ex,
                    "The payment ids Holded accepted for SEPA transfer {TransferId} could not be saved; the "
                    + "audit entry is the only record of them.", transferId);
            }
        }

        // Holded keeps no tag on a document payment, so the synced ledger's payment lines on the creditor account
        // dated this line are the only record of what earlier runs paid. A count that differs from the ids Humans
        // saved (a POST that failed after Holded saved it, a run that died before saving its ids, an id gone) means
        // the amount still owed is unknown, so the booking stops.
        string? PriorPaymentsRefusal(IReadOnlyList<HoldedLedgerLineInfo> lines, LocalDate lineDate, int account)
        {
            var onDay = lines
                .Where(l => string.Equals(l.Type, "payment", StringComparison.Ordinal)
                            && l.Date.InZone(OrgZone).Date == lineDate)
                .ToList();
            if (onDay.Count != priorRefs.Count)
                return $"Holded's ledger shows {onDay.Count} document payment(s) on creditor account {account} dated "
                       + $"{lineDate.ToInvariantDate()} ({Euros(onDay.Sum(l => l.Debit - l.Credit))}), but Humans "
                       + $"recorded {priorRefs.Count} — settle it in Holded by hand.";

            priorPaid = onDay.Sum(l => l.Debit - l.Credit);
            return null;
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
            // Part of the line is already settled by hand; posting the whole transfer would pay some documents twice.
            if (!IsUntouched(m))
                return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                       + "Nothing was posted.";

            // Text naming another account is another transfer's line. No text at all is how
            // Sabadell books these, so the line is matched on amount and date instead.
            var account = RemittanceAccountNum(m.Description);
            if (account is not null && account.Value != transfer.SupplierAccountNum)
                return $"That Sabadell line's text names creditor account {account.Value}, not "
                       + $"{transfer.SupplierAccountNum} — nothing was posted.";
            if (account is null && TextlessLineFitsAFile(all, m, tolerance))
                return "That Sabadell line names no creditor account and could be a whole file's debit "
                       + "as well as this transfer's — settle it in Holded by hand. Nothing was posted.";

            if (all.Any(r => r.TransferId != transfer.Id
                             && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
                return "That Sabadell line already booked another transfer — nothing was posted.";

            var matches = UnbookedMatches(all, account, transfer.Amount, m.Date, tolerance);
            if (matches.Count > 1)
            {
                // The page's set pairing, re-run: its own rival check stands in for the one below.
                var own = account is null
                    ? TextlessSetPairs(all, lines, m, tolerance)
                        .FirstOrDefault(p => string.Equals(p.Line.Id, m.Id, StringComparison.Ordinal)).Row
                    : null;
                if (own is null)
                    return $"{matches.Count} unbooked transfers match that Sabadell line — Humans cannot tell "
                           + "which it paid; settle it in Holded by hand. Nothing was posted.";
                return own.TransferId == transfer.Id
                    ? null
                    : "That Sabadell line does not match this transfer — nothing was posted.";
            }
            if (matches.Count != 1 || matches[0].TransferId != transfer.Id)
                return "That Sabadell line does not match this transfer — nothing was posted.";

            // Another unreconciled line of the same amount, dated no earlier than this file, naming this account or
            // none, could equally have paid it.
            var rivals = lines.Count(x =>
                !string.Equals(x.Id, m.Id, StringComparison.Ordinal)
                && NeedsAMatch(x)
                && Math.Abs(x.Amount) == transfer.Amount
                && RemittanceAccountNum(x.Description) is var xAccount
                && (xAccount is null || xAccount == transfer.SupplierAccountNum)
                && !all.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                && UnbookedMatches(all, xAccount, transfer.Amount, x.Date, tolerance)
                    .Any(r => r.TransferId == transfer.Id));
            return rivals == 0
                ? null
                : $"{rivals + 1} Sabadell lines could each have paid this transfer — Humans cannot tell "
                  + "which one did; settle it in Holded by hand. Nothing was posted.";
        }
    }

    /// <summary>When every euro of the line is named, Holded accepting the call is the reconcile; otherwise it stamps
    /// only once Holded reports <c>reconciled</c>, else the row stays "reconcile pending" for the sweep. A list Holded
    /// refuses outright is re-sent in chunks of <see cref="ReconcileChunkSize"/>, always followed by a re-read.</summary>
    private async Task<Instant?> TryReconcileAsync(
        string movementId, LocalDate movementDate, List<HoldedReconcileDocumentRef> docs,
        bool incomplete, CancellationToken ct)
    {
        if (docs.Count == 0) return null;

        var chunked = false;
        try
        {
            await holdedWrite.ReconcileBankMovementAsync(sepa.Value.TreasuryAccountId, movementId, docs, ct);
        }
        catch (HoldedPermanentException ex) when (docs.Count > ReconcileChunkSize)
        {
            // A refusal moved no money and linked nothing, so the same ids can go again in chunks.
            logger.LogWarning(
                "Holded refused the {Count}-payment reconcile of bank movement {MovementId} in one call; "
                + "re-sending it in chunks of {Chunk}. {Reason}", docs.Count, movementId, ReconcileChunkSize, ex.Message);
            chunked = true;
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("Holded refused to reconcile bank movement {MovementId}. {Reason}", movementId, ex.Message);
            return null;
        }

        var refused = new List<HoldedReconcileDocumentRef>();
        if (chunked)
        {
            // A refused chunk does not stop the rest: one bad id must not strand the payments behind it.
            var number = 0;
            foreach (var chunk in docs.Chunk(ReconcileChunkSize))
            {
                number++;
                try
                {
                    await holdedWrite.ReconcileBankMovementAsync(sepa.Value.TreasuryAccountId, movementId, chunk, ct);
                }
                catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                {
                    logger.LogWarning(
                        "Holded refused chunk {Chunk} ({Size} payment(s)) of the reconcile of bank movement "
                        + "{MovementId}. {Reason}", number, chunk.Length, movementId, ex.Message);
                    refused.AddRange(chunk);
                }
            }

            if (refused.Count > 0 && refused.Count < docs.Count)
                // The line is now `partial`, which the sweep never re-issues, so the refused ids are a human's.
                logger.LogWarning(
                    "Holded linked {Linked} of {Count} payment(s) to bank movement {MovementId} and refused "
                    + "{Refused}: {Ids}. The line reads partial, which the sweep does not re-issue — finish "
                    + "it in Holded by hand.",
                    docs.Count - refused.Count, docs.Count, movementId, refused.Count,
                    string.Join(",", refused.Select(d => d.DocumentId)));
        }

        return incomplete || chunked
            ? await StampIfReconciledAsync(movementId, movementDate, docs, incomplete || refused.Count > 0, ct)
            : clock.GetCurrentInstant();
    }

    private static List<string> SplitRefs(string? joined) =>
        (joined ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool HasUnconfirmedRef(string? joined) =>
        SplitRefs(joined).Any(r => r.StartsWith("unconfirmed:", StringComparison.Ordinal));

    /// <summary>Leaves out <c>unconfirmed:</c> sentinels (never replaced by the document id) and the <c>entry:</c>
    /// token: Holded cannot link a ledger entry to a bank line.</summary>
    private static List<HoldedReconcileDocumentRef> PaymentRefs(string? joined) =>
        (joined ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !r.StartsWith("unconfirmed:", StringComparison.Ordinal)
                        && !r.StartsWith(SepaPayoutTransferRow.JournalEntryRefPrefix, StringComparison.Ordinal))
            .Select(r => new HoldedReconcileDocumentRef(r, HoldedReconcileDocumentType.Payment))
            .ToList();

    /// <summary>For a reconcile that cannot be trusted on acceptance alone (a remainder left out, or chunks): asks
    /// Holded, and anything but <c>reconciled</c> stays pending. A line still <c>partial</c> after every euro was named
    /// is logged as a finding; a known remainder is only noted.</summary>
    private async Task<Instant?> StampIfReconciledAsync(
        string movementId, LocalDate movementDate, List<HoldedReconcileDocumentRef> docs,
        bool incomplete, CancellationToken ct)
    {
        HoldedBankMovementDto? line;
        try
        {
            var after = await ReadFeedAsync(movementDate, movementDate, SkippedStuckLineIds(), ct);
            line = after.FirstOrDefault(m => string.Equals(m.Id, movementId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(
                "Could not re-read bank movement {MovementId} after reconciling it; the transfer stays "
                + "reconcile pending. {Reason}", movementId, ex.Message);
            return null;
        }

        if (line is not null && IsReconciled(line)) return clock.GetCurrentInstant();

        if (line is not null && !incomplete && !IsUntouched(line))
            logger.LogWarning(
                "Holded took every payment of bank movement {MovementId} ({Amount}) but the line reads "
                + "{Status}; the {Count} id(s) sent: {Ids}. The transfer stays reconcile pending — "
                + "settle the line in Holded by hand.",
                movementId, Euros(line.Amount), line.Status, docs.Count,
                string.Join(",", docs.Select(d => d.DocumentId)));
        else
            logger.LogInformation(
                "Holded took the reconcile for bank movement {MovementId} but the line reads {Status}; the "
                + "transfer stays reconcile pending.", movementId,
                line?.Status ?? "gone");
        return null;
    }

    private LocalDate Today() => clock.GetCurrentInstant().InZone(OrgZone).Date;

    /// <summary>The oldest non-stale file's first payable day under <paramref name="tolerance"/>, so every line
    /// <see cref="UnbookedMatches"/> could accept is read.</summary>
    private LocalDate FeedFrom(int tolerance) => Today().PlusDays(-FeedWindowDays - tolerance);

    /// <summary>Skips the lines the sweep has given up on, unless the caller (the sweep's own counting read) names the
    /// set itself.</summary>
    private Task<IReadOnlyList<HoldedBankMovementDto>> ReadBankFeedAsync(
        LocalDate from, CancellationToken ct, IReadOnlySet<string>? skipUnreadableDateIds = null) =>
        ReadFeedAsync(from, Today(), skipUnreadableDateIds ?? SkippedStuckLineIds(), ct);

    /// <summary>Syncs the window, then reads it from the mirror: never the mirror alone, since every caller decides
    /// whether money moves on what it reads. A sync already running throws as transient, like any unreadable feed.</summary>
    private async Task<IReadOnlyList<HoldedBankMovementDto>> ReadFeedAsync(
        LocalDate from, LocalDate to, IReadOnlySet<string> skipUnreadableDateIds, CancellationToken ct)
    {
        if (!await holded.SyncBankMovementsAsync(sepa.Value.TreasuryAccountId, from, to, skipUnreadableDateIds, ct))
            throw new HoldedTransientException("A Holded bank-feed sync is already running; try again in a minute.");
        return (await holded.GetBankMovementsAsync(sepa.Value.TreasuryAccountId, from, to, ct)).Movements;
    }

    /// <summary>Every read of the feed skips these, or the booking the sweep just unblocked would fail on the same
    /// line.</summary>
    private HashSet<string> SkippedStuckLineIds() =>
        bookingState.TryGet(CacheKeys.SepaStuckBankLines, out var runs)
            ? runs.Where(kv => kv.Value >= StuckLineSkipAfterRuns).Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>The SEPA identity generated the transfers; the treasury account, or Holded pays from its default; its
    /// ledger account, because the remainder entry must credit the same bank.</summary>
    private string? BookingUnavailableReason()
    {
        if (sepa.Value.GetPayoutSettings().UnavailableReason is { } sepaMissing) return sepaMissing;
        if (string.IsNullOrWhiteSpace(sepa.Value.TreasuryAccountId))
            return "Booking is unavailable — Sepa:TreasuryAccountId is not configured.";
        return sepa.Value.TreasuryLedgerAccount is null
            ? "Booking is unavailable — Sepa:TreasuryLedgerAccount is not configured."
            : null;
    }

    /// <summary>Approved and still owing. A draft books nothing to the ledger, so paying one posts against nothing.</summary>
    internal static List<HoldedPurchaseDocInfo> OpenDocs(
        IReadOnlyList<HoldedPurchaseDocInfo> docs) =>
        docs.Where(d => d.IsApproved && d.PaymentsPending > 0m).ToList();

    private const string ReconciledStatus = "reconciled";

    private const string PendingStatus = "pending";

    /// <summary>Matched anywhere in the line, not anchored: bank feeds prepend their own wording.</summary>
    private static readonly Regex RemittanceAccount = new(
        @"(?<acct>\d{8})\s*-\s*NCA\s*-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary><c>partial</c> counts as started: part of that line is settled by hand, so posting the whole transfer
    /// would pay those documents twice.</summary>
    internal static bool NeedsAMatch(HoldedBankMovementDto m) => m.Amount < 0m && IsUntouched(m);

    /// <summary>Holded reports <c>pending</c>, <c>partial</c> or <c>reconciled</c>; only the first has nothing tied
    /// to it.</summary>
    internal static bool IsUntouched(HoldedBankMovementDto m) =>
        string.Equals(m.Status, PendingStatus, StringComparison.Ordinal);

    internal static bool IsReconciled(HoldedBankMovementDto m) =>
        string.Equals(m.Status, ReconciledStatus, StringComparison.Ordinal);

    /// <summary>Null when the line is not one of ours.</summary>
    internal static int? RemittanceAccountNum(string? description)
    {
        var match = RemittanceAccount.Match(description ?? "");
        return match.Success
            && int.TryParse(match.Groups["acct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
            ? num
            : null;
    }

    /// <summary>A withdrawn transfer was cancelled at the bank, so no line pays it and no file total includes it. Every
    /// pairing path reads through this.</summary>
    internal static List<SepaPayoutTransferRow> Live(IEnumerable<SepaPayoutTransferRow> rows) =>
        rows.Where(r => !r.IsWithdrawn).ToList();

    /// <summary>Any account when the line names none (Sabadell drops the remittance text). More than one is ambiguous.
    /// Only files inside the feed window count, or an old row would make every later same-amount transfer ambiguous
    /// forever; a line dated before its file is some older payment.</summary>
    internal List<SepaPayoutTransferRow> UnbookedMatches(
        IReadOnlyList<SepaPayoutTransferRow> rows, int? account, decimal amount, LocalDate lineDate,
        int tolerance) =>
        rows.Where(r => r.BookedAt is null && !r.IsWithdrawn && (account is null || r.SupplierAccountNum == account)
                        && r.Amount == amount && !IsStale(r)
                        && InWindow(r, lineDate, tolerance, bounded: account is null))
            .ToList();

    /// <summary>No more than <paramref name="tolerance"/> days before the file date, and for a line only the date ties
    /// (<paramref name="bounded"/>, textless) no more than that after. A line naming the account may be later.</summary>
    private bool InWindow(SepaPayoutTransferRow row, LocalDate lineDate, int tolerance, bool bounded)
    {
        var fileDate = FileDate(row);
        return lineDate >= fileDate.PlusDays(-tolerance)
               && (!bounded || lineDate <= fileDate.PlusDays(tolerance));
    }

    /// <summary>A textless line that is also a multi-transfer file's total inside its window: one transfer's debit
    /// cannot be told from a whole file's, so neither path takes it.</summary>
    internal bool TextlessLineFitsAFile(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId).Any(f =>
            f.Count() >= 2 && f.Sum(r => r.Amount) == Math.Abs(m.Amount)
            && f.Any(r => !r.IsBooked) && !IsStale(f.First())
            // A row booked against some other line means the bank did not debit this file as
            // one — the same rule BatchLineRefusal applies.
            && !f.Any(r => r.IsBooked && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal))
            && InWindow(f.First(), m.Date, tolerance, bounded: true));

    /// <summary>
    /// Pairs a textless set (two or more same-amount transfers of one file) one to one with exactly as many textless
    /// lines, when no other line could pay any of them. Which line paid which is unknowable and accounting-neutral, so
    /// lines go by id against transfers by account. Empty when the set does not pair.
    /// </summary>
    internal List<(HoldedBankMovementDto Line, SepaPayoutTransferRow Row)> TextlessSetPairs(
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        HoldedBankMovementDto m, int tolerance)
    {
        var set = UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance);
        if (set.Count < 2 || set.Select(r => r.FileId).Distinct().Count() > 1) return [];
        var ids = set.Select(r => r.TransferId).ToHashSet();
        var claimants = feed
            .Where(x => NeedsAMatch(x)
                        && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal)))
            .Select(x => (Line: x, Account: RemittanceAccountNum(x.Description)))
            .Select(x => (x.Line, x.Account, Matches:
                UnbookedMatches(rows, x.Account, Math.Abs(x.Line.Amount), x.Line.Date, tolerance)
                    .Select(r => r.TransferId).ToHashSet()))
            .Where(x => x.Matches.Overlaps(ids))
            .ToList();
        if (claimants.Count != set.Count
            || claimants.Any(x => x.Account is not null || !x.Matches.SetEquals(ids)
                                  || TextlessLineFitsAFile(rows, x.Line, tolerance)))
            return [];
        return claimants
            .OrderBy(x => x.Line.Id, StringComparer.Ordinal)
            .Zip(set.OrderBy(r => r.SupplierAccountNum), (x, r) => (x.Line, r))
            .ToList();
    }

    /// <summary>More than one is ambiguous and waits for a human.</summary>
    internal List<Guid> BatchFiles(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m,
        IReadOnlyList<HoldedBankMovementDto> feed, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId)
            .Where(g => BatchLineRefusal(g.ToList(), m, rows, feed, tolerance) is null)
            .Select(g => g.Key)
            .ToList();

    /// <summary>Judged on this line and file alone; other files and lines are the caller's ambiguity check. The amount
    /// must be the file's total, exactly: nothing is split by guesswork.</summary>
    internal string? BatchLineRefusal(
        IReadOnlyList<SepaPayoutTransferRow> file, HoldedBankMovementDto m,
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        int tolerance)
    {
        var total = file.Sum(r => r.Amount);
        if (file.Count < 2)
            return "That file holds one transfer — book it on its own row. Nothing was posted.";
        if (m.Amount >= 0m || Math.Abs(m.Amount) != total)
            return $"That Sabadell line is {Euros(m.Amount)}, not the {Euros(total)} this file totals — "
                   + "nothing was posted.";
        if (!IsUntouched(m))
            return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                   + "Nothing was posted.";
        if (IsStale(file[0]))
            return $"That file was generated more than {FeedWindowDays} days ago — settle it in Holded "
                   + "by hand. Nothing was posted.";
        if (!InWindow(file[0], m.Date, tolerance, bounded: true))
            return $"That Sabadell line is dated more than {tolerance} day(s) from when this file was "
                   + "generated — widen the tolerance if it is this file's. Nothing was posted.";
        if (file.All(r => r.IsBooked))
            return "Every transfer in that file is already booked — nothing was posted.";
        // A transfer booked against a line of its own means the bank did not debit this file as one.
        if (file.Any(r => r.IsBooked
                          && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "Part of that file was already booked against its own Sabadell lines — settle the "
                   + "rest in Holded by hand. Nothing was posted.";
        if (rows.Any(r => r.FileId != file[0].FileId
                          && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "That Sabadell line already booked a transfer from another file — nothing was posted.";
        // A line whose text names one transfer's account and amount is that transfer's own line.
        if (RemittanceAccountNum(m.Description) is { } account)
        {
            if (UnbookedMatches(rows, account, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
                return "That Sabadell line names a single transfer — book it on that transfer's row. "
                       + "Nothing was posted.";
        }
        // A textless one that is also some single transfer's amount could be either.
        else if (UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
            return "That Sabadell line names no creditor account and could be a single transfer's "
                   + "debit as well as this file's — settle it in Holded by hand. Nothing was posted.";
        // And the mirror: a transfer of this file with a line of its own was not debited as part of
        // one total, so a total-sized line is some other payment.
        if (feed.Any(x => NeedsAMatch(x)
                          && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                          && RemittanceAccountNum(x.Description) is { } own
                          && UnbookedMatches(file, own, Math.Abs(x.Amount), x.Date, tolerance).Count > 0))
            return "A transfer in that file has a Sabadell line of its own — book the transfers on their "
                   + "rows. Nothing was posted.";
        return null;
    }

    /// <summary>The file's generation day, less the generation tolerance.</summary>
    internal LocalDate FirstPayableDate(SepaPayoutTransferRow row) =>
        FileDate(row).PlusDays(-GenerationSlackDays);

    /// <summary>In the organisation's zone: what a bank line's date is measured against.</summary>
    internal LocalDate FileDate(SepaPayoutTransferRow row) => row.GeneratedAt.InZone(OrgZone).Date;

    /// <summary>Generated before the feed's window, so no line can book it: a human's job in Holded.</summary>
    public bool IsStale(SepaPayoutTransferRow row) =>
        row.GeneratedAt.InZone(OrgZone).Date < clock.GetCurrentInstant().InZone(OrgZone).Date.PlusDays(-FeedWindowDays);

    /// <summary>
    /// After the save: an outbox row is a promise of money. Sent at generation, when the treasurer hands the file to
    /// the bank, not at booking. A failure here must not block the download of the saved file.
    /// </summary>
    public async Task SendPayoutEmailsAsync(IReadOnlyList<SepaPayoutTransfer> transfers)
    {
        var ct = CancellationToken.None;
        var userIds = transfers.Select(t => t.UserId);
        IReadOnlyDictionary<Guid, UserInfo> infos;
        try
        {
            infos = await users.GetUserInfosAsync(userIds, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve recipients for saved SEPA payout {FileId}", transfers[0].FileId);
            return;
        }

        foreach (var t in transfers)
        {
            infos.TryGetValue(t.UserId, out var member);
            var recipient = member?.Email;
            if (member is null || string.IsNullOrWhiteSpace(recipient))
            {
                logger.LogWarning(
                    "Skipping SEPA payout email for transfer {TransferId}: member {UserId} has no notification email",
                    t.Id, t.UserId);
                continue;
            }

            try
            {
                var language = member.PreferredLanguage;
                await emailService.SendAsync(emails.SepaPayoutGenerated(
                    member.Id,
                    recipient, member.BurnerName, t.Amount, t.IbanMasked,
                    language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue email for saved SEPA payout transfer {TransferId}", t.Id);
            }
        }
    }

    /// <summary>The member was told a transfer was coming, so they are told it was cancelled. After the save, with no
    /// request token.</summary>
    public async Task SendWithdrawnEmailAsync(SepaPayoutTransfer t, LocalDate announcedOn)
    {
        var ct = CancellationToken.None;
        try
        {
            var infos = await users.GetUserInfosAsync([t.UserId], ct);
            infos.TryGetValue(t.UserId, out var member);
            var recipient = member?.Email;
            if (member is null || string.IsNullOrWhiteSpace(recipient))
            {
                logger.LogWarning(
                    "Skipping SEPA withdrawal email for transfer {TransferId}: member {UserId} has no notification email",
                    t.Id, t.UserId);
                return;
            }

            var language = member.PreferredLanguage;
            await emailService.SendAsync(emails.SepaPayoutWithdrawn(
                member.Id,
                recipient, member.BurnerName, t.Amount, t.IbanMasked, announcedOn,
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue email for withdrawn SEPA payout transfer {TransferId}", t.Id);
        }
    }

    // Persisted in audit_log.entity_type; pinned so a rename cannot silently orphan old rows.

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
                     + $"{now.InZone(OrgZone).ToDateTimeUnspecified().ToFileTimestamp()}-"
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
        await SendPayoutEmailsAsync(transfers);

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
            RequestedExecutionDate: now.InZone(OrgZone).Date,
            Debtor: new SepaDebtor(
                sepa.Value.CreditorName!, sepa.Value.CreditorIban!,
                sepa.Value.CreditorBic, sepa.Value.CreditorIdentifier!),
            MaxAmountPerTransfer: maxPerTransfer,
            Transfers: transfers
                .Select(t => new SepaTransfer(t.CreditorName, t.Iban, t.Amount, t.SupplierAccountNum))
                .ToList())
        { CreationTimeZone = OrgZone };

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

    /// <summary>Safe in a download filename.</summary>
    private static string FileSlug(string name)
    {
        var cleaned = new string(SepaText.Normalize(name, 32).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray());
        var slug = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "sepa" : slug;
    }
}
