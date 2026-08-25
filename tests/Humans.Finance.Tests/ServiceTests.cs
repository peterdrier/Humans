using System.Text.Json;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

public class HoldedFinanceServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 5, 1, 12, 0);

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IBudgetServiceRead _budget = Substitute.For<IBudgetServiceRead>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly FakeClock _clock = new(FixedNow);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly SepaOptions _sepa = new();

    private Service MakeService() => new(
        _repo,
        _client,
        _budget,
        _holded,
        _clock,
        _cache,
        _audit,
        Options.Create(_sepa),
        NullLogger<Service>.Instance);

    /// <summary>Same service, with a logger the test can read back.</summary>
    private Service MakeService(ILogger<Service> logger) => new(
        _repo, _client, _budget, _holded, _clock, _cache, _audit, Options.Create(_sepa), logger);

    /// <summary>Ledger-line stub for the mirror reads (positional: entry, line, account, date, type, description, debit, credit).</summary>
    private static HoldedLedgerLineInfo Line(int entry, int line, int account, Instant date,
        decimal debit = 0m, decimal credit = 0m, string? type = null) =>
        new(entry, line, account, date, type, null, debit, credit);

    // ─── GetActualsForYear ────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetActualsForYear_SumsGrossDocTotals_ApprovedOnly()
    {
        var cat = Guid.NewGuid();
        _repo.GetMatchedForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(new List<HoldedExpenseDoc>
        {
            // The budget pages are gross/IVA-inclusive, so the actual is Total, not Subtotal.
            new() { HoldedDocId = "d1", BudgetCategoryId = cat, Subtotal = 100m, Total = 121m, IsApproved = true },
            new() { HoldedDocId = "d2", BudgetCategoryId = cat, Subtotal = 50m, Total = 60.50m, IsApproved = true },
            new() { HoldedDocId = "d3", BudgetCategoryId = cat, Total = 999m, IsApproved = false },  // draft
            new() { HoldedDocId = "d4", BudgetCategoryId = cat, Total = 500m, IsApproved = null },   // pre-v2 row
            new() { HoldedDocId = "d5", BudgetCategoryId = null, Total = 77m, IsApproved = true },
        });

        var rows = await MakeService().GetActualsForYearAsync(2026, Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.Should().Be(new HoldedActualRow(cat, 181.50m));
    }

    // ─── GetProvisioningPlan ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetProvisioningPlan_marks_categories_without_accounts_as_ToAdd()
    {
        var catIdA = Guid.NewGuid();
        var catIdB = Guid.NewGuid();

        // Active year has two categories in one group.
        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(
            Id: Guid.NewGuid(),
            Year: "2026",
            Name: "Camp 2026",
            Status: BudgetYearStatus.Active,
            IsDeleted: false,
            Groups:
            [
                new BudgetGroupDetail(
                    Id: Guid.NewGuid(),
                    BudgetYearId: Guid.NewGuid(),
                    Name: "Operations",
                    SortOrder: 1,
                    IsRestricted: false,
                    IsDepartmentGroup: false,
                    IsTicketingGroup: false,
                    TicketingProjection: null,
                    Categories:
                    [
                        new BudgetCategoryDetail(catIdA, Guid.NewGuid(), "Staff", 0, ExpenditureType.OpEx, null, 0, []),
                        new BudgetCategoryDetail(catIdB, Guid.NewGuid(), "Toilets", 0, ExpenditureType.OpEx, null, 1, []),
                    ])
            ]));

        // Map already contains an active row for catA; catB has no map entry.
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new List<HoldedCategoryMap>
            {
                new()
                {
                    Id = Guid.NewGuid(), BudgetCategoryId = catIdA,
                    HoldedAccountNumber = 6290001, HoldedAccountId = "acc-1",
                    Tag = "operationsstaff", IsActive = true,
                    CreatedAt = FixedNow, UpdatedAt = FixedNow,
                }
            });

        var svc = MakeService();
        var plan = await svc.GetProvisioningPlanAsync(blockStart: 6290010, ct: Xunit.TestContext.Current.CancellationToken);

        var mapped = plan.Rows.Where(r => string.Equals(r.State, "Mapped", StringComparison.Ordinal)).ToList();
        var toAdd = plan.Rows.Where(r => string.Equals(r.State, "ToAdd", StringComparison.Ordinal)).ToList();

        mapped.Should().HaveCount(1);
        mapped[0].BudgetCategoryId.Should().Be(catIdA);
        mapped[0].ExistingAccountNum.Should().Be(6290001);

        toAdd.Should().HaveCount(1);
        toAdd[0].BudgetCategoryId.Should().Be(catIdB);
        toAdd[0].ProposedAccountNum.Should().Be(6290010); // first free >= blockStart
        toAdd[0].State.Should().Be("ToAdd");
        toAdd[0].Tag.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public async Task GetProvisioningPlan_skips_account_numbers_occupied_in_holded()
    {
        var catId = Guid.NewGuid();

        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(
            Id: Guid.NewGuid(),
            Year: "2026",
            Name: "Camp 2026",
            Status: BudgetYearStatus.Active,
            IsDeleted: false,
            Groups:
            [
                new BudgetGroupDetail(
                    Id: Guid.NewGuid(),
                    BudgetYearId: Guid.NewGuid(),
                    Name: "Operations",
                    SortOrder: 1,
                    IsRestricted: false,
                    IsDepartmentGroup: false,
                    IsTicketingGroup: false,
                    TicketingProjection: null,
                    Categories:
                    [
                        new BudgetCategoryDetail(catId, Guid.NewGuid(), "Staff", 0, ExpenditureType.OpEx, null, 0, []),
                    ])
            ]));

        // Local map is empty …
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedCategoryMap>());

        // … but Holded already has an account at the first block number.
        _client.ListExpenseAccountsAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new List<HoldedExpenseAccountDto>
            {
                new() { Id = "acc-x", AccountNum = 6290010, Name = "Existing" },
            });

        var svc = MakeService();
        var plan = await svc.GetProvisioningPlanAsync(blockStart: 6290010, ct: Xunit.TestContext.Current.CancellationToken);

        var toAdd = plan.Rows.Single(r => string.Equals(r.State, "ToAdd", StringComparison.Ordinal));
        toAdd.ProposedAccountNum.Should().Be(6290011); // 6290010 is taken in Holded → skipped
    }

    // ─── Sync ─────────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Sync_attributes_by_account_then_tag_and_counts()
    {
        var catId = Guid.NewGuid();

        // One active map entry: account "acc-1", tag "comms".
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new List<HoldedCategoryMap>
            {
                new()
                {
                    Id = Guid.NewGuid(), BudgetCategoryId = catId,
                    HoldedAccountNumber = 6290001, HoldedAccountId = "acc-1",
                    Tag = "comms", IsActive = true,
                    CreatedAt = FixedNow, UpdatedAt = FixedNow,
                }
            });

        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new HoldedDocSyncState());

        var docDate = Instant.FromUtc(2026, 4, 15, 10, 0);

        // 3 docs: account match, tag match, unmatched. d1 is approved; d2 is still a draft.
        var docs1 = new List<HoldedPurchaseDocListItemDto>
        {
            new()
            {
                Id = "d1", DocNumber = "F001", ContactName = "Alice", Date = docDate,
                Subtotal = 100, Tax = 21, Total = 121, Currency = "eur", IsDraft = false,
                Lines = [new HoldedPurchaseLineDto { Amount = 100, AccountId = "acc-1", Tags = [] }],
                Tags = [],
            },
            new()
            {
                Id = "d2", DocNumber = "F002", ContactName = "Bob", Date = docDate,
                Subtotal = 50, Tax = 0, Total = 50, Currency = "eur", IsDraft = true,
                Lines = [new HoldedPurchaseLineDto { Amount = 50, AccountId = "acc-generic", Tags = [] }],
                Tags = ["comms"],   // tag match
            },
            new()
            {
                Id = "d3", DocNumber = "F003", ContactName = "Carol", Date = docDate,
                Subtotal = 30, Tax = 0, Total = 30, Currency = "eur",
                Lines = [new HoldedPurchaseLineDto { Amount = 30, AccountId = "acc-generic", Tags = [] }],
                Tags = ["nope"],    // no match
            },
        };

        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((IReadOnlyList<HoldedPurchaseDocListItemDto>)docs1);

        IReadOnlyList<HoldedExpenseDoc>? capturedDocs = null;
        await _repo.UpsertDocsAsync(
            Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => capturedDocs = d),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());

        var svc = MakeService();
        var result = await svc.SyncAsync(Xunit.TestContext.Current.CancellationToken);

        result.DocCount.Should().Be(3);
        result.Matched.Should().Be(2);
        result.Unmatched.Should().Be(1);

        capturedDocs.Should().NotBeNull();
        capturedDocs!.Should().HaveCount(3);

        var d1 = capturedDocs.Single(d => string.Equals(d.HoldedDocId, "d1", StringComparison.Ordinal));
        d1.MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        d1.MatchSource.Should().Be(HoldedMatchSource.Account);
        d1.BudgetCategoryId.Should().Be(catId);
        d1.IsApproved.Should().BeTrue();

        var d2 = capturedDocs.Single(d => string.Equals(d.HoldedDocId, "d2", StringComparison.Ordinal));
        d2.MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        d2.MatchSource.Should().Be(HoldedMatchSource.Tag);
        d2.BudgetCategoryId.Should().Be(catId);
        d2.IsApproved.Should().BeFalse();

        var d3 = capturedDocs.Single(d => string.Equals(d.HoldedDocId, "d3", StringComparison.Ordinal));
        d3.MatchStatus.Should().Be(HoldedMatchStatus.Unmatched);
        d3.MatchSource.Should().Be(HoldedMatchSource.None);
        d3.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task Sync_sets_error_state_on_exception()
    {
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedCategoryMap>());

        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new HoldedDocSyncState());

        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Holded API unavailable"));

        HoldedDocSyncState? savedState = null;
        await _repo.SaveDocSyncStateAsync(
            Arg.Do<HoldedDocSyncState>(s => savedState = s),
            Arg.Any<CancellationToken>());

        var svc = MakeService();
        var act = () => svc.SyncAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The last saved state must be Error.
        savedState.Should().NotBeNull();
        savedState!.Status.Should().Be("Error");
        savedState.LastError.Should().NotBeNullOrEmpty();
    }

    // ─── Creditor data (derived from the cached daybook ledger, via the Holded section) ────

    [HumansFact]
    public async Task GetCreditorStatus_derives_balance_owed_and_payments_from_lines()
    {
        // Daniela 40000001: credit 12720 (in) − debit 9540 (paid) ⇒ balance −3180, owed 3180.
        // Two lines a side, deliberately: with one each, a sum and a max are the same number.
        // The last debit sits at 22:30Z, which is the next day in Madrid — the date the member sees.
        _holded.GetLedgerLinesAsync(40000001, Arg.Any<CancellationToken>()).Returns(new List<HoldedLedgerLineInfo>
        {
            Line(1, 0, 40000001, Instant.FromUtc(2026, 4, 1, 0, 0), credit: 12000m, type: "purchase"),
            Line(2, 0, 40000001, Instant.FromUtc(2026, 4, 5, 0, 0), credit: 720m, type: "purchase"),
            Line(3, 0, 40000001, Instant.FromUtc(2026, 4, 10, 0, 0), debit: 9000m, type: "payment"),
            Line(4, 0, 40000001, Instant.FromUtc(2026, 4, 20, 22, 30), debit: 540m, type: "payment"),
            // Booked after the last payment, and not one: only debit lines are money going out.
            Line(5, 0, 40000001, Instant.FromUtc(2026, 4, 25, 0, 0), credit: 0m, debit: 0m, type: "purchase"),
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, Xunit.TestContext.Current.CancellationToken);

        status.Should().NotBeNull();
        status!.Balance.Should().Be(-3180m);
        status.OwedToMember.Should().Be(3180m);
        status.TotalPaid.Should().Be(9540m);                 // debit lines only, summed
        status.LastPaymentDate.Should().Be(new LocalDate(2026, 4, 21));
    }

    [HumansFact]
    public async Task GetCreditorStatus_returns_null_when_no_lines_cached()
    {
        _holded.GetLedgerLinesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<HoldedLedgerLineInfo>());

        var status = await MakeService().GetCreditorStatusAsync(40000099, Xunit.TestContext.Current.CancellationToken);

        status.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCreditorStatus_returns_null_when_account_num_null()
    {
        var status = await MakeService().GetCreditorStatusAsync(null, Xunit.TestContext.Current.CancellationToken);

        status.Should().BeNull();
    }

    [HumansFact]
    public async Task ListCreditorAccounts_DerivesFromLedger_AndSurfacesBothSidesOfACollision()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -10m });
        // Two members on the same account number — only UserId is unique in the DB, and the automatic
        // push paths record what Holded assigned rather than refusing, so this state is reachable.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = first, HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
            new() { UserId = second, HoldedContactId = "c2", SupplierAccountNum = 40000004, Source = CreditorContactSource.Auto },
        });

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle(r => r.SupplierAccountNum == 40000004).Subject;
        row.Balance.Should().Be(-10m);
        row.OwedToMember.Should().Be(10m);
        // Both, not the first: hiding the second is what kept the collision invisible to admins.
        row.Bindings.Select(b => b.UserId).Should().BeEquivalentTo([first, second]);
    }

    [HumansFact]
    public async Task ListCreditorAccounts_BindingWithUnresolvedNumber_LandsOnItsAccountViaTheContactId()
    {
        // The one-shot number resolution is best-effort and has no retry, so a binding can sit on a
        // contact id with a null 400000xx indefinitely. Keyed on the number alone the account would
        // render "unbound" while this member holds it — and could not be unbound from that page.
        var userId = Guid.NewGuid();
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal>());
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = null, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
        });

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.SupplierAccountNum.Should().Be(40000004);
        row.Bindings.Should().ContainSingle().Which.UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task ListCreditorAccounts_BindingsSharingAContact_CollideOnOneRowDespiteDifferentNumbers()
    {
        // HoldedContactId and SupplierAccountNum are independent columns, so two members on one Holded
        // contact can carry numbers that disagree — the legacy state the previously-unguarded seed path
        // could leave behind. Grouped on the stored number they would be two innocent single-member
        // rows, hiding the contact-id half of the invariant FindConflictingBinding enforces on writes.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal>());
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = first, HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
            new() { UserId = second, HoldedContactId = "c1", SupplierAccountNum = 40000007, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
        });

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        // Holded says c1 is 40000004, so that is the row — and 40000007 was only ever a stale guess,
        // with no ledger lines and no contact of its own to keep it alive.
        var row = rows.Should().ContainSingle().Subject;
        row.SupplierAccountNum.Should().Be(40000004);
        row.Bindings.Select(b => b.UserId).Should().BeEquivalentTo([first, second]);
    }

    // ─── ListCreditorAccounts: the Unresolved half ─────────────────────────────────

    [HumansFact]
    public async Task ListCreditorAccounts_BindingWithNoNumberAndNoLiveHoldedNumber_IsUnresolved()
    {
        // Neither the one-shot push resolution nor Holded's own contact list carries a number for this
        // contact, so there is no account row to place it on — it comes back in Unresolved (#972).
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = null, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = null },
        });

        var (_, unresolved) = await MakeService()
            .ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        unresolved.Should().ContainSingle().Which.UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task ListCreditorAccounts_BindingResolvedViaLiveHoldedContact_IsNotUnresolved()
    {
        // Holded's own contact list carries a number for this contact even though our cached
        // SupplierAccountNum is null — it lands on that account's row, so the same binding must not
        // also come back in Unresolved. The two halves are a partition, not two overlapping filters.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = null, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
        });

        var (_, unresolved) = await MakeService()
            .ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        unresolved.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ListCreditorAccounts_FullyBoundMember_IsNotUnresolved()
    {
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>());

        var (_, unresolved) = await MakeService()
            .ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        unresolved.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCreditorLedger_derives_balance_and_lines_from_cache()
    {
        _holded.GetLedgerLinesAsync(40000004, Arg.Any<CancellationToken>()).Returns(new List<HoldedLedgerLineInfo>
        {
            Line(1, 0, 40000004, FixedNow, credit: 50m),
        });

        var ledger = await MakeService().GetCreditorLedgerAsync(40000004, Xunit.TestContext.Current.CancellationToken);

        ledger.Should().NotBeNull();
        ledger.Balance.Should().Be(-50m);
        ledger.OwedToMember.Should().Be(50m);
        ledger.Lines.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetCreditorLedger_carries_the_contact_header_when_a_cached_contact_holds_the_account()
    {
        _holded.GetLedgerLinesAsync(40000004, Arg.Any<CancellationToken>()).Returns(new List<HoldedLedgerLineInfo>
        {
            Line(1, 0, 40000004, FixedNow, credit: 50m),
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new()
            {
                Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004, TradeName = "Dani",
                Email = "dani@example.org", Phone = "+34 600 000 000", Mobile = "+34 611 111 111",
                Iban = "ES9121000418450200051332", TaxCode = "X1234567L", Address = "Calle Mayor 1, 28013 Madrid",
            },
            new() { Id = "c2", Name = "Someone Else", SupplierAccountNum = 40000007 },
        });

        var ledger = await MakeService().GetCreditorLedgerAsync(40000004, Xunit.TestContext.Current.CancellationToken);

        ledger.Should().NotBeNull();
        ledger.Contact.Should().NotBeNull();
        ledger.Contact.Name.Should().Be("Daniela Marquez");
        ledger.Contact.TradeName.Should().Be("Dani");
        ledger.Contact.Email.Should().Be("dani@example.org");
        ledger.Contact.Phone.Should().Be("+34 600 000 000");
        ledger.Contact.Mobile.Should().Be("+34 611 111 111");
        ledger.Contact.Iban.Should().Be("ES9121000418450200051332");
        ledger.Contact.TaxCode.Should().Be("X1234567L");
        ledger.Contact.Address.Should().Be("Calle Mayor 1, 28013 Madrid");
    }

    [HumansFact]
    public async Task GetCreditorLedger_leaves_the_contact_header_null_when_no_cached_contact_matches()
    {
        // Holded unreachable, or an account whose contact carries a different number: the statement
        // still renders from the mirror, just without a header.
        _holded.GetLedgerLinesAsync(40000004, Arg.Any<CancellationToken>()).Returns(new List<HoldedLedgerLineInfo>
        {
            Line(1, 0, 40000004, FixedNow, credit: 50m),
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c2", Name = "Someone Else", SupplierAccountNum = 40000007 },
        });

        var ledger = await MakeService().GetCreditorLedgerAsync(40000004, Xunit.TestContext.Current.CancellationToken);

        ledger.Should().NotBeNull();
        ledger.Contact.Should().BeNull();
        ledger.Balance.Should().Be(-50m);
    }

    [HumansFact]
    public async Task GetCreditorLedger_returns_null_when_no_lines()
    {
        _holded.GetLedgerLinesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<HoldedLedgerLineInfo>());

        var ledger = await MakeService().GetCreditorLedgerAsync(40000004, Xunit.TestContext.Current.CancellationToken);

        ledger.Should().BeNull();
    }

    // ─── EnsureCreditorContact (binding write path) ──────────────────────────────

    [HumansFact]
    public async Task EnsureCreditorContact_NewMember_CreatesContact_AndRecordsAutoBinding()
    {
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("new-contact");

        var id = await MakeService().EnsureCreditorContactAsync(
            userId, "Maria Garcia", "Meri", "ES9121000418450200051332", null, null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("new-contact");
        // No binding/seed -> POST create (ExistingContactId null); legal->Name, burner->TradeName, userId->CustomId.
        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i =>
                i.Name == "Maria Garcia" &&
                i.TradeName == "Meri" &&
                i.CustomId == userId.ToString() &&
                i.Type == "creditor" &&
                i.ExistingContactId == null),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c =>
                c.UserId == userId && c.HoldedContactId == "new-contact" &&
                c.Source == CreditorContactSource.Auto),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_BurnerSameAsLegal_OmitsTradeName()
    {
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Maria Garcia", "Maria Garcia", null, null, null,
            Xunit.TestContext.Current.CancellationToken);

        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.TradeName == null), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_ExistingBinding_ReusesContactIdAsUpdate()
    {
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "existing-c",
                SupplierAccountNum = 40000004,
                Source = CreditorContactSource.Auto,
            });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("existing-c");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Peter Drier", null, null, null, null,
            Xunit.TestContext.Current.CancellationToken);

        // Existing binding -> PUT update (ExistingContactId set), never a duplicate create.
        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.ExistingContactId == "existing-c"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_NoBindingButSeed_AdoptsSeedContactId()
    {
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("seed-c");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Peter Drier", null, null, "seed-c", 40000004,
            Xunit.TestContext.Current.CancellationToken);

        // Lazy-seed from a prior pushed report -> PUT update on the seeded contact, not a new create.
        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.ExistingContactId == "seed-c"), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.SupplierAccountNum == 40000004),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_ManualBinding_NotDowngradedToAuto()
    {
        // A Manual binding whose 400000xx never resolved: the seed still has a number to contribute,
        // so this is a push that genuinely writes — which is what makes it the case that can downgrade.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "m-c",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Manual,
            });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("m-c");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Daniela Marquez", null, null, null, 40000001,
            Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.Source == CreditorContactSource.Manual),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_MemberAlreadyHoldsThisContact_WritesNothing()
    {
        // The steady state: bound member, number already resolved. UpsertContactAsync PUTs to the id it
        // was given and returns it, and Source/number come straight off the binding just read, so the
        // row's only changing column would be UpdatedAt — which nothing reads. It is not a harmless
        // write either: it lands after a multi-second Holded round-trip carrying a pre-round-trip copy
        // of the binding, so an admin who unbinds during that window would have the binding they just
        // cleared resurrected. No write, nothing to resurrect.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c1",
                SupplierAccountNum = 40000004,
                Source = CreditorContactSource.Auto,
            });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c1");

        var id = await MakeService().EnsureCreditorContactAsync(
            userId, "Peter Drier", null, null, null, null,
            Xunit.TestContext.Current.CancellationToken);

        // Holded is still updated — the member's legal name and IBAN have to reach the contact.
        id.Should().Be("c1");
        await _client.Received(1).UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_BindingMissingItsAccountNumber_StillWritesTheSeedNumber()
    {
        // The skip must not swallow a push that has something to say: an unresolved 400000xx is exactly
        // the gap the seed exists to close, so this write still has to happen.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c1",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
            });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c1");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Peter Drier", null, null, null, 40000004,
            Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.SupplierAccountNum == 40000004),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_UnboundDuringHoldedRoundTrip_DoesNotResurrectBinding()
    {
        // nobodies-collective/Humans#995: the write below still carries real content (an unresolved
        // account number), so it cannot be skipped the way the steady state is — but an Unbind landing
        // during the Holded round trip above must still not come back from the dead.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c1",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
            },
            (HoldedCreditorContact?)null); // re-check right before the write: admin unbound mid-push
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c1");

        var id = await MakeService().EnsureCreditorContactAsync(
            userId, "Peter Drier", null, null, null, 40000004,
            Xunit.TestContext.Current.CancellationToken);

        // Holded itself was still updated — only the local binding write is skipped.
        id.Should().Be("c1");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_ReboundDuringHoldedRoundTrip_DoesNotClobberTheNewBinding()
    {
        // An admin who unbinds *and* rebinds inside the window leaves a row behind, so mere presence
        // proves nothing — and a rebind of an already-bound member keeps the row and its Id
        // (UpsertCreditorContactAsync is keyed by UserId), so Id proves nothing either. Only the
        // columns this write would overwrite say whether it is still the binding that was read.
        var userId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = bindingId,
                UserId = userId,
                HoldedContactId = "c1",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
            },
            new HoldedCreditorContact                  // re-check: admin rebound to another contact
            {
                Id = bindingId,
                UserId = userId,
                HoldedContactId = "c2",
                SupplierAccountNum = 40000009,
                Source = CreditorContactSource.Manual,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c1");

        var id = await MakeService(logger)
            .EnsureCreditorContactAsync(
                userId, "Peter Drier", null, null, null, 40000004,
                Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("c1");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        // The table has no audit trail, so the skip is only reconstructible from this line.
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("c2");
    }

    [HumansFact]
    public async Task EnsureCreditorContact_BoundByAdminDuringHoldedRoundTrip_DoesNotOverwriteTheManualBind()
    {
        // Same window, opposite polarity: nothing was bound at the opening read, so the write below is
        // a first-time insert — but Upsert is keyed by UserId, and a manual bind landing mid-push turns
        // that insert into an update that overwrites the admin's contact and drops Source back to Auto.
        var userId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            (HoldedCreditorContact?)null,
            new HoldedCreditorContact                  // re-check: admin bound them while we pushed
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c-admin",
                SupplierAccountNum = 40000009,
                Source = CreditorContactSource.Manual,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-new");

        var id = await MakeService(logger)
            .EnsureCreditorContactAsync(
                userId, "Peter Drier", null, null, null, null,
                Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("c-new");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("c-admin");
    }

    [HumansFact]
    public async Task SetCreditorAccountNum_RecordsAssignedAccountOnExistingBinding()
    {
        var userId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = bindingId,
                UserId = userId,
                HoldedContactId = "new-contact",
                SupplierAccountNum = null,          // first push: Holded had not assigned one yet
                Source = CreditorContactSource.Auto,
            });

        await MakeService().SetCreditorAccountNumAsync(
            userId, 40000012, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c =>
                c.Id == bindingId && c.UserId == userId &&
                c.HoldedContactId == "new-contact" && c.SupplierAccountNum == 40000012 &&
                c.Source == CreditorContactSource.Auto),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCreditorAccountNum_UnboundBetweenReadAndWrite_DoesNotResurrectBinding()
    {
        // nobodies-collective/Humans#995: this runs at the end of a long push, well after an admin
        // could have clicked Unbind. The window between the read and the write below has no I/O in it,
        // but the re-check still must catch a delete that lands there.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "new-contact",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
            },
            (HoldedCreditorContact?)null); // re-check right before the write: admin unbound in between

        await MakeService().SetCreditorAccountNumAsync(
            userId, 40000012, Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCreditorAccountNum_UnboundAndReboundBetweenReadAndWrite_DoesNotClobberTheNewBinding()
    {
        // An Unbind followed by a fresh manual bind leaves a row in place, so the write is not blocked
        // by absence — and it would carry the old binding's contact id and Source over the admin's.
        var userId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "new-contact",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
            },
            new HoldedCreditorContact                  // re-check: a different row — deleted, rebound
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c-admin",
                SupplierAccountNum = 40000009,
                Source = CreditorContactSource.Manual,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());

        await MakeService(logger)
            .SetCreditorAccountNumAsync(userId, 40000012, Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("c-admin");
    }

    // ─── Creditor account names + manual bind guard ──────────────────────────────

    [HumansFact]
    public async Task ListCreditorAccounts_NamesRowsFromHolded_AndIncludesContactsWithNoLedgerActivity()
    {
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -40m });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
            new() { Id = "c2", Name = "Maria Garcia", SupplierAccountNum = 40000012 },  // no ledger lines yet
            new() { Id = "c3", Name = "A Client", SupplierAccountNum = null },          // not a supplier
            new() { Id = "c4", Name = "Acme Supplies SL", SupplierAccountNum = 42000000 }, // vendor, outside the block
        });

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        // Ordinary org vendors carry supplier numbers too — only the 40000000–41999999 member-creditor
        // block counts, or an admin could bind a member onto a vendor's account.
        rows.Should().HaveCount(2);
        rows.Should().NotContain(r => r.SupplierAccountNum == 42000000);
        rows.Should().ContainSingle(r => r.SupplierAccountNum == 40000004)
            .Which.Name.Should().Be("Daniela Marquez");
        // A first-time submitter's contact exists in Holded before any journal activity — still selectable.
        var fresh = rows.Should().ContainSingle(r => r.SupplierAccountNum == 40000012).Subject;
        fresh.Name.Should().Be("Maria Garcia");
        fresh.Balance.Should().BeNull();
    }

    [HumansFact]
    public async Task ListCreditorAccounts_HoldedUnavailable_StillReturnsCachedRowsWithBlankNames()
    {
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -40m });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        // The real client wraps HTTP failures — assert against what production actually throws.
        _client.ListContactsAsync(Arg.Any<CancellationToken>())
            .Throws(new HoldedTransientException("Holded is down"));

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.SupplierAccountNum.Should().Be(40000004);
        row.OwedToMember.Should().Be(40m);
        row.Name.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ListCreditorAccounts_UnexpectedClientFailure_Propagates()
    {
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal>());
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        // Only vendor-call failures degrade to blank names; a bug must not be silently absorbed.
        _client.ListContactsAsync(Arg.Any<CancellationToken>())
            .Throws(new NullReferenceException("mapping bug"));

        var act = async () => await MakeService()
            .ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NullReferenceException>();
    }

    [HumansFact]
    public async Task ListCreditorAccounts_UnreadableHoldedResponse_DegradesToBlankNames()
    {
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -40m });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        // A malformed body is still a vendor failure — /Finance/Creditors has no try/catch, so letting
        // this escape would 500 the page instead of costing the names.
        _client.ListContactsAsync(Arg.Any<CancellationToken>())
            .Throws(new JsonException("unexpected token"));

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.Name.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetCreditorContact_AccountOutsideCreditorBlock_FailsWithoutTouchingHolded()
    {
        // The number arrives on a POST; the filtered dropdown is not a server-side gate.
        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), 42000000, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("outside the member creditor block");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().ListContactsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ListCreditorAccounts_BoundAccountWithNoHoldedContact_YieldsRowWithBlankName()
    {
        var userId = Guid.NewGuid();
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal>());
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>());

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        // The binding keeps the row visible; there is simply no name to show for it.
        var row = rows.Should().ContainSingle().Subject;
        row.SupplierAccountNum.Should().Be(40000004);
        row.Bindings.Should().ContainSingle().Which.UserId.Should().Be(userId);
        row.Name.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetCreditorContact_AccountBoundToAnotherMember_FailsAndWritesNothing()
    {
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
        });

        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), 40000004, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already bound to a different member");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().ListContactsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCreditorContact_ContactBoundToAnotherMemberWithUnresolvedAccount_FailsAndWritesNothing()
    {
        // The other member's binding carries the contact but no account number — the push resolves it
        // best-effort — so the account-number guard cannot see the collision. The contact-id guard must.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c1", SupplierAccountNum = null, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
        });

        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), 40000004, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Daniela Marquez");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCreditorContact_NoHoldedContactCarriesAccount_FailsAndWritesNothing()
    {
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>());

        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), 40000004, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No Holded contact");
        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCreditorContact_RebindingOwnAccount_Succeeds()
    {
        var userId = Guid.NewGuid();
        // The member's own existing binding on this account must not read as a conflict.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Daniela Marquez", SupplierAccountNum = 40000004 },
        });

        var result = await MakeService().SetCreditorContactAsync(
            userId, 40000004, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c =>
                c.UserId == userId && c.HoldedContactId == "c1" &&
                c.SupplierAccountNum == 40000004 && c.Source == CreditorContactSource.Manual),
            FixedNow, Arg.Any<CancellationToken>());
    }

    // ─── The automatic write paths (nobodies-collective/Humans#975) ──────────────
    //
    // Holded assigned the number to the contact just pushed, so it is authoritative and the older
    // binding is the wrong guess. Refusing would leave a real payable unlinked to preserve a bad row,
    // so these paths write, log Error, and leave the collision standing on /Finance/Creditors.

    [HumansFact]
    public async Task SetCreditorAccountNum_AccountHeldByAnotherMember_WritesAnyway_AndLogsError()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c-mine",
                SupplierAccountNum = null,
                Source = CreditorContactSource.Auto,
                CreatedAt = FixedNow,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = otherUserId, HoldedContactId = "c-theirs", SupplierAccountNum = 40000012, Source = CreditorContactSource.Manual },
        });

        await MakeService(logger)
            .SetCreditorAccountNumAsync(userId, 40000012, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.UserId == userId && c.SupplierAccountNum == 40000012),
            FixedNow, Arg.Any<CancellationToken>());
        var error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain(otherUserId.ToString()).And.Contain("40000012");
    }

    [HumansFact]
    public async Task SetCreditorAccountNum_AccountFree_WritesWithoutLoggingAnError()
    {
        var userId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "c-mine",
                Source = CreditorContactSource.Auto,
                CreatedAt = FixedNow,
            });
        // The member's own prior binding on the same account must not read as a collision.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c-mine", SupplierAccountNum = 40000012, Source = CreditorContactSource.Auto },
        });

        await MakeService(logger)
            .SetCreditorAccountNumAsync(userId, 40000012, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Any<HoldedCreditorContact>(), FixedNow, Arg.Any<CancellationToken>());
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [HumansFact]
    public async Task EnsureCreditorContact_SeedHeldByAnotherMember_IsRefused_AndANewContactIsCreated()
    {
        // The seed is this member's own cached guess off a prior report, not something Holded just
        // told us, so it is refused like a bad manual bind rather than recorded like an assignment.
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = otherUserId, HoldedContactId = "c-theirs", SupplierAccountNum = 40000012, Source = CreditorContactSource.Manual },
        });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-fresh");

        await MakeService(logger)
            .EnsureCreditorContactAsync(
                userId, "Ana Ruiz", null, null, seedContactId: "c-theirs", seedAccountNum: 40000012,
                Xunit.TestContext.Current.CancellationToken);

        // No ExistingContactId => Holded POSTs a new contact, splitting the two members apart.
        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.ExistingContactId == null), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c =>
                c.UserId == userId && c.HoldedContactId == "c-fresh" && c.SupplierAccountNum == null),
            FixedNow, Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain(otherUserId.ToString());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_SeedContactHeldByAnotherMember_IsRefused_EvenWithNoAccountNumber()
    {
        // The one-shot number resolution is best-effort, so the other member's binding can carry the
        // shared Holded contact with a null 400000xx — invisible to an account-number-only check.
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var logger = new CapturingLogger<Service>();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = otherUserId, HoldedContactId = "c-shared", SupplierAccountNum = null, Source = CreditorContactSource.Auto },
        });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-fresh");

        await MakeService(logger)
            .EnsureCreditorContactAsync(
                userId, "Ana Ruiz", null, null, seedContactId: "c-shared", seedAccountNum: null,
                Xunit.TestContext.Current.CancellationToken);

        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.ExistingContactId == null), Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain("c-shared");
    }

    [HumansFact]
    public async Task EnsureCreditorContact_AfterUnbind_DoesNotRestoreTheOtherMembersBindingFromAPriorReport()
    {
        // The regression this guards (PR #1194 review): unbind deletes the binding row, but the
        // cleared member's old reports still carry the other member's contact id and 400000xx, and
        // ProcessHoldedCreateAsync hands those straight back as seeds on their very next push.
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = otherUserId, HoldedContactId = "c-theirs", SupplierAccountNum = 40000012, Source = CreditorContactSource.Manual },
        });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-fresh");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Ana Ruiz", null, null, seedContactId: "c-theirs", seedAccountNum: 40000012,
            Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.HoldedContactId == "c-theirs" || c.SupplierAccountNum == 40000012),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureCreditorContact_SeedIsTheMembersOwnContact_IsStillAdopted()
    {
        // The lazy-seed self-heal must survive the refusal: only *another* member's binding blocks it.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns((HoldedCreditorContact?)null);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c-mine", SupplierAccountNum = 40000012, Source = CreditorContactSource.Auto },
        });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-mine");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Ana Ruiz", null, null, seedContactId: "c-mine", seedAccountNum: 40000012,
            Xunit.TestContext.Current.CancellationToken);

        await _client.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i => i.ExistingContactId == "c-mine"), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.SupplierAccountNum == 40000012),
            FixedNow, Arg.Any<CancellationToken>());
    }

    // ─── Unbind ─────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task ClearCreditorContact_RemovesTheBinding()
    {
        var userId = Guid.NewGuid();
        _repo.DeleteCreditorContactAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        var removed = await MakeService().ClearCreditorContactAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        removed.Should().BeTrue();
        await _repo.Received(1).DeleteCreditorContactAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ClearCreditorContact_NothingBound_ReportsFalse()
    {
        var userId = Guid.NewGuid();
        _repo.DeleteCreditorContactAsync(userId, Arg.Any<CancellationToken>()).Returns(false);

        var removed = await MakeService().ClearCreditorContactAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        removed.Should().BeFalse();
    }

    // ─── Unmatched worklist ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetUnmatched_TellsTheTreasurerWhichHalfOfTheChainMissed()
    {
        // The reason text is the whole point of the worklist: it says where to go fix the mapping.
        _repo.GetUnmatchedAsync(Arg.Any<CancellationToken>()).Returns(
        [
            UnmatchedDoc("neither", account: null, tagsJson: "[]"),
            UnmatchedDoc("both", account: "acc-9", tagsJson: """["ops"]"""),
            UnmatchedDoc("account-only", account: "acc-9", tagsJson: "[]"),
            UnmatchedDoc("tags-only", account: null, tagsJson: """["ops"]"""),
        ]);

        var rows = await MakeService().GetUnmatchedAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Select(r => (r.DocNumber, r.Reason)).Should().Equal(
            ("neither", "No account, no tag"),
            ("both", "Account and tags not mapped"),
            ("account-only", "Account not mapped"),
            ("tags-only", "Tags not matched"));
        rows[0].HoldedUrl.Should().Be("https://app.holded.com/purchases/doc-neither");
    }

    private static HoldedExpenseDoc UnmatchedDoc(string docNumber, string? account, string tagsJson) => new()
    {
        Id = Guid.NewGuid(),
        HoldedDocId = $"doc-{docNumber}",
        DocNumber = docNumber,
        ContactName = "Vendor",
        Date = new LocalDate(2026, 4, 1),
        Total = 10m,
        Currency = "eur",
        TagsJson = tagsJson,
        BookedAccountId = account,
        MatchStatus = HoldedMatchStatus.Unmatched,
        MatchSource = HoldedMatchSource.None,
        CreatedAt = FixedNow,
        UpdatedAt = FixedNow,
    };

    // ─── Provisioning: tag collisions ────────────────────────────────────────────

    [HumansFact]
    public async Task GetProvisioningPlan_TagTakenByAMappedRowFurtherDownTheWalk_IsStillDisambiguated()
    {
        // "S-taff" normalizes to the same tag as "Staff" and sorts ahead of it, so a ToAdd row
        // reaches the collision check before the mapped row that already owns the tag. Two active
        // rows sharing a tag make tag attribution arbitrary, so the proposal must not reuse it.
        var toAddCat = Guid.NewGuid();
        var mappedCat = Guid.NewGuid();
        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(
            Id: Guid.NewGuid(), Year: "2026", Name: "Camp 2026",
            Status: BudgetYearStatus.Active, IsDeleted: false,
            Groups:
            [
                new BudgetGroupDetail(
                    Id: Guid.NewGuid(), BudgetYearId: Guid.NewGuid(), Name: "Operations",
                    SortOrder: 1, IsRestricted: false, IsDepartmentGroup: false,
                    IsTicketingGroup: false, TicketingProjection: null,
                    Categories:
                    [
                        new BudgetCategoryDetail(toAddCat, Guid.NewGuid(), "S-taff", 0, ExpenditureType.OpEx, null, 0, []),
                        new BudgetCategoryDetail(mappedCat, Guid.NewGuid(), "Staff", 0, ExpenditureType.OpEx, null, 1, []),
                    ])
            ]));
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new List<HoldedCategoryMap>
            {
                new()
                {
                    Id = Guid.NewGuid(), BudgetCategoryId = mappedCat,
                    HoldedAccountNumber = 6290001, HoldedAccountId = "acc-1",
                    Tag = "operationsstaff", IsActive = true,
                    CreatedAt = FixedNow, UpdatedAt = FixedNow,
                }
            });

        var plan = await MakeService().GetProvisioningPlanAsync(
            blockStart: 6290010, ct: Xunit.TestContext.Current.CancellationToken);

        var proposed = plan.Rows.Single(r => r.BudgetCategoryId == toAddCat);
        proposed.State.Should().Be("ToAdd");
        proposed.Tag.Should().NotBe("operationsstaff");
        proposed.Tag.Should().StartWith("operationsstaff");
    }

    // ─── Provisioning: applying the plan ─────────────────────────────────────────

    [HumansFact]
    public async Task Provision_AddOne_CreatesExactlyTheFirstPendingAccount()
    {
        var (catA, catB) = TwoUnmappedCategories();
        _client.CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"acc-{ci.ArgAt<int>(0)}");

        var created = await MakeService().ProvisionAsync(
            blockStart: 6290010, addAll: false, ct: Xunit.TestContext.Current.CancellationToken);

        created.Should().Be(1);
        await _client.Received(1).CreateExpenseAccountAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var row = _repo.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IHoldedRepository.AddCategoryMapAsync), StringComparison.Ordinal))
            .Select(c => (HoldedCategoryMap)c.GetArguments()[0]!)
            .Should().ContainSingle().Subject;
        row.BudgetCategoryId.Should().Be(catA);          // "Alpha" sorts first
        row.HoldedAccountNumber.Should().Be(6290010);
        row.HoldedAccountId.Should().Be("acc-6290010");
        row.IsActive.Should().BeTrue();
        row.Tag.Should().NotBeNullOrEmpty();
        catB.Should().NotBe(catA);
    }

    [HumansFact]
    public async Task Provision_AddAll_CreatesEveryPendingAccountAndNeverRemovesAMapRow()
    {
        TwoUnmappedCategories();
        _client.CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"acc-{ci.ArgAt<int>(0)}");

        var created = await MakeService().ProvisionAsync(
            blockStart: 6290010, addAll: true, ct: Xunit.TestContext.Current.CancellationToken);

        created.Should().Be(2);
        var numbers = _repo.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IHoldedRepository.AddCategoryMapAsync), StringComparison.Ordinal))
            .Select(c => ((HoldedCategoryMap)c.GetArguments()[0]!).HoldedAccountNumber);
        numbers.Should().Equal(6290010, 6290011);
        // Provisioning is additive: the repository has no map-delete method and none is called.
        _repo.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().NotContain(n => n.Contains("Delete", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Provision_HoldedRefusesTheSecondAccount_KeepsTheFirstAndRethrows()
    {
        // Partial success is deliberate: an account already created in Holded must keep its map row,
        // or the number is stranded remotely with nothing pointing at it.
        TwoUnmappedCategories();
        _client.CreateExpenseAccountAsync(6290010, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("acc-6290010");
        _client.CreateExpenseAccountAsync(6290011, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("holded said no"));

        var act = async () => await MakeService().ProvisionAsync(
            blockStart: 6290010, addAll: true, ct: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.Received(1).AddCategoryMapAsync(
            Arg.Is<HoldedCategoryMap>(m => m.HoldedAccountNumber == 6290010), Arg.Any<CancellationToken>());
    }

    /// <summary>An active year with two unmapped categories, "Alpha" then "Beta", and an empty map.</summary>
    private (Guid First, Guid Second) TwoUnmappedCategories()
    {
        var catA = Guid.NewGuid();
        var catB = Guid.NewGuid();
        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(
            Id: Guid.NewGuid(), Year: "2026", Name: "Camp 2026",
            Status: BudgetYearStatus.Active, IsDeleted: false,
            Groups:
            [
                new BudgetGroupDetail(
                    Id: Guid.NewGuid(), BudgetYearId: Guid.NewGuid(), Name: "Operations",
                    SortOrder: 1, IsRestricted: false, IsDepartmentGroup: false,
                    IsTicketingGroup: false, TicketingProjection: null,
                    Categories:
                    [
                        new BudgetCategoryDetail(catA, Guid.NewGuid(), "Alpha", 0, ExpenditureType.OpEx, null, 0, []),
                        new BudgetCategoryDetail(catB, Guid.NewGuid(), "Beta", 0, ExpenditureType.OpEx, null, 1, []),
                    ])
            ]));
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new List<HoldedCategoryMap>());
        return (catA, catB);
    }

    // ─── The creditor block is inclusive at both ends ────────────────────────────

    [HumansFact]
    public async Task ListCreditorAccounts_TheCreditorBlockIsInclusiveAtBothEnds()
    {
        // Holded numbers every supplier contact, so the block bounds are the only thing separating a
        // member's creditor account from an ordinary org vendor. Both ends are members' accounts.
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal>
            {
                [39999999] = -1m,
                [40000000] = -1m,
                [41999999] = -1m,
                [42000000] = -1m,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "lo-out", Name = "Vendor", SupplierAccountNum = 39999999 },
            new() { Id = "lo-in", Name = "First member", SupplierAccountNum = 40000000 },
            new() { Id = "hi-in", Name = "Last member", SupplierAccountNum = 41999999 },
            new() { Id = "hi-out", Name = "Vendor", SupplierAccountNum = 42000000 },
        });

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Select(r => r.SupplierAccountNum).Should().BeEquivalentTo([40000000, 41999999]);
        // Name comes from the contact filter and Balance from the mirror filter; assert both, or a row
        // arriving through one of them hides the other end being wrong.
        var first = rows.Single(r => r.SupplierAccountNum == 40000000);
        var last = rows.Single(r => r.SupplierAccountNum == 41999999);
        first.Name.Should().Be("First member");
        last.Name.Should().Be("Last member");
        first.Balance.Should().Be(-1m);
        last.Balance.Should().Be(-1m);
    }

    [HumansTheory]
    [InlineData(39999999)]
    [InlineData(42000000)]
    public async Task SetCreditorContact_JustOutsideTheBlock_IsRefused(int accountNum)
    {
        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), accountNum, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        await _client.DidNotReceiveWithAnyArgs().ListContactsAsync(Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(40000000)]
    [InlineData(41999999)]
    public async Task SetCreditorContact_OnTheBlockBoundary_IsAccepted(int accountNum)
    {
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Member", SupplierAccountNum = accountNum },
        });

        var result = await MakeService().SetCreditorContactAsync(
            Guid.NewGuid(), accountNum, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
    }

    // ─── Negative rule: the sync never removes a doc ─────────────────────────────

    [HumansFact]
    public async Task Sync_DocsMissingFromHolded_AreNeverDeleted()
    {
        // "The sync job cannot delete HoldedExpenseDoc rows" — Holded-side deletions are out of scope,
        // so a doc that stops appearing in the pull is simply not upserted, never removed.
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedCategoryMap>());
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState());
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedPurchaseDocListItemDto>());

        var result = await MakeService().SyncAsync(Xunit.TestContext.Current.CancellationToken);

        result.DocCount.Should().Be(0);
        _repo.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().NotContain(n => n.Contains("Delete", StringComparison.Ordinal));
    }

    // ─── Which Holded account an expense line is booked to ───────────────────────

    [HumansFact]
    public async Task GetHoldedAccountIdForCategory_UsesTheActiveMapping_AndIgnoresAnArchivedOne()
    {
        // Expenses calls this to book a purchase line straight onto the category's account. An
        // archived row for the same category must never win, and an unmapped category yields null.
        var mapped = Guid.NewGuid();
        var unmapped = Guid.NewGuid();
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new List<HoldedCategoryMap>
            {
                new()
                {
                    Id = Guid.NewGuid(), BudgetCategoryId = mapped, HoldedAccountNumber = 6290001,
                    HoldedAccountId = "acc-old", Tag = "a", IsActive = false,
                    CreatedAt = FixedNow, UpdatedAt = FixedNow,
                },
                new()
                {
                    Id = Guid.NewGuid(), BudgetCategoryId = mapped, HoldedAccountNumber = 6290002,
                    HoldedAccountId = "acc-live", Tag = "b", IsActive = true,
                    CreatedAt = FixedNow, UpdatedAt = FixedNow,
                },
            });

        var svc = MakeService();
        (await svc.GetHoldedAccountIdForCategoryAsync(mapped, Xunit.TestContext.Current.CancellationToken))
            .Should().Be("acc-live");
        (await svc.GetHoldedAccountIdForCategoryAsync(unmapped, Xunit.TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    // ─── GDPR export slice ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task ContributeForUser_BoundMember_ExportsTheBindingAndNothingElse()
    {
        // Article 15: the member gets what we hold about them. The binding row is three facts —
        // and not the row's Id or timestamps, which are ours rather than theirs.
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "contact-9",
                SupplierAccountNum = 40000004,
                Source = CreditorContactSource.Manual,
                CreatedAt = FixedNow,
                UpdatedAt = FixedNow,
            });

        var slices = await MakeService().ContributeForUserAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        var slice = slices.Should()
            .ContainSingle(s => s.SectionName == GdprExportSections.HoldedCreditorAccount).Subject;
        slice.Data.Should().NotBeNull();
        var json = JsonSerializer.Serialize(slice.Data);
        json.Should().Contain("40000004").And.Contain("contact-9").And.Contain("Manual");
        json.Should().NotContain("CreatedAt").And.NotContain("UpdatedAt");
    }

    [HumansFact]
    public async Task ContributeForUser_UnboundMember_StillReportsTheSectionWithNoData()
    {
        // The section has to appear either way: "we hold nothing here" is itself the answer, and a
        // missing section reads as a section nobody asked.
        _repo.GetCreditorContactByUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((HoldedCreditorContact?)null);

        var slices = await MakeService().ContributeForUserAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var slice = slices.Should()
            .ContainSingle(s => s.SectionName == GdprExportSections.HoldedCreditorAccount).Subject;
        slice.Data.Should().BeNull();
    }

    [HumansFact]
    public async Task ContributeForUser_IncludesEveryPayoutWithTheIbanMasked()
    {
        var userId = Guid.NewGuid();
        _repo.GetSepaPayoutsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<SepaPayoutExportRow>
            {
                new(FixedNow, "nobodies-collective-2026-08-25-0309-4f1a9c02.xml", 40000004, "Ana Ruiz", "ES79****789", 12.34m),
            });

        var slices = await MakeService().ContributeForUserAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        var slice = slices.Should()
            .ContainSingle(s => s.SectionName == GdprExportSections.SepaPayouts).Subject;
        var json = JsonSerializer.Serialize(slice.Data);
        json.Should().Contain("ES79****789").And.Contain("12.34")
            .And.NotContain(AnaIban, "the export masks the IBAN even though the payout row keeps it raw");
    }

    // ─── Purchase-doc sync state, as the /Holded screen reads it ─────────────────

    [HumansFact]
    public async Task GetDocSyncInfo_ReportsTheStoredStateAndTheBindingCount()
    {
        // The binding count rides along on this DTO so /Holded can show a number without building
        // the whole creditor view, which would cost a live Holded contacts walk.
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(
            new HoldedDocSyncState
            {
                LastSyncAt = FixedNow,
                Status = "Error",
                LastError = "holded said no",
                LastSyncedDocCount = 42,
            });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), HoldedContactId = "c1" },
            new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), HoldedContactId = "c2" },
        });

        var info = await MakeService().GetDocSyncInfoAsync(Xunit.TestContext.Current.CancellationToken);

        info.LastSyncAt.Should().Be(FixedNow);
        info.Status.Should().Be("Error");
        info.LastError.Should().Be("holded said no");
        info.LastSyncedDocCount.Should().Be(42);
        info.CreditorBindingCount.Should().Be(2);
    }

    [HumansFact]
    public async Task GetDocSyncInfo_NeverSynced_ReadsAsIdleWithNothingRecorded()
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState());
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>());

        var info = await MakeService().GetDocSyncInfoAsync(Xunit.TestContext.Current.CancellationToken);

        info.Status.Should().Be("Idle");
        info.LastSyncAt.Should().BeNull();
        info.LastError.Should().BeNull();
        info.LastSyncedDocCount.Should().Be(0);
        info.CreditorBindingCount.Should().Be(0);
    }

    // ─── GetConnectorOverview (/Finance/Holded) ───────────────────────────────────

    /// <summary>Seeds the active year with one group holding the given categories.</summary>
    private void ActiveYearWith(params (Guid Id, string Name)[] categories) =>
        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(
            Id: Guid.NewGuid(),
            Year: "2026",
            Name: "Camp 2026",
            Status: BudgetYearStatus.Active,
            IsDeleted: false,
            Groups:
            [
                new BudgetGroupDetail(
                    Id: Guid.NewGuid(),
                    BudgetYearId: Guid.NewGuid(),
                    Name: "Operations",
                    SortOrder: 1,
                    IsRestricted: false,
                    IsDepartmentGroup: false,
                    IsTicketingGroup: false,
                    TicketingProjection: null,
                    Categories: categories
                        .Select((c, i) => new BudgetCategoryDetail(
                            c.Id, Guid.NewGuid(), c.Name, 0, ExpenditureType.OpEx, null, i, []))
                        .ToList())
            ]));

    private void SeedConnector(
        HoldedDocSyncState? state = null,
        IReadOnlyList<HoldedCategoryMap>? map = null,
        IReadOnlyList<HoldedExpenseDoc>? docs = null,
        IReadOnlyList<HoldedCreditorContact>? bindings = null)
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(state ?? new HoldedDocSyncState());
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>())
            .Returns(map ?? []);
        _repo.GetAllDocsAsync(Arg.Any<CancellationToken>())
            .Returns(docs ?? []);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>())
            .Returns(bindings ?? []);
    }

    [HumansFact]
    public async Task GetConnectorOverview_MakesNoHoldedApiCall()
    {
        // The whole point of the page (nobodies-collective/Humans#1000): /Finance/Creditors already
        // pays a live ListContactsAsync on every load behind a 30 s timeout, and the index must not
        // inherit that latency or that failure mode.
        ActiveYearWith();
        SeedConnector();

        await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(_client.ReceivedCalls());
    }

    [HumansFact]
    public async Task GetConnectorOverview_SyncWithinTheWindow_IsNotStale()
    {
        ActiveYearWith();
        SeedConnector(new HoldedDocSyncState
        {
            LastSyncAt = FixedNow - Duration.FromHours(5),
            Status = "Idle",
            LastSyncedDocCount = 7,
        });

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.DocSync.IsStale.Should().BeFalse();
        vm.DocSync.SinceLastSync.Should().Be(Duration.FromHours(5));
        vm.DocSync.LastSyncedDocCount.Should().Be(7);
    }

    [HumansFact]
    public async Task GetConnectorOverview_SyncOlderThanTheWindow_IsStale()
    {
        ActiveYearWith();
        SeedConnector(new HoldedDocSyncState { LastSyncAt = FixedNow - Duration.FromHours(40) });

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.DocSync.IsStale.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetConnectorOverview_NeverSynced_IsStaleWithNoAge()
    {
        // Never having run leaves the actuals and the unmatched queue as wrong as a stalled sync
        // does, so it raises the same alarm rather than rendering a reassuring blank.
        ActiveYearWith();
        SeedConnector(new HoldedDocSyncState());

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.DocSync.IsStale.Should().BeTrue();
        vm.DocSync.SinceLastSync.Should().BeNull();
        vm.DocSync.LastSyncAt.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConnectorOverview_AFailedRun_KeepsTheSuccessTimeAndCarriesTheAttemptTime()
    {
        // SyncAsync stamps StatusChangedAt on failure and deliberately leaves LastSyncAt on the
        // older success. Dropping StatusChangedAt made an Error row read as "nothing has run since
        // the success" — the opposite of the truth, on the page that exists to say what is running.
        var succeeded = FixedNow - Duration.FromHours(50);
        var failed = FixedNow - Duration.FromHours(2);
        ActiveYearWith();
        SeedConnector(new HoldedDocSyncState
        {
            LastSyncAt = succeeded,
            Status = "Error",
            LastError = "holded said no",
            StatusChangedAt = failed,
            LastSyncedDocCount = 12,
        });

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.DocSync.LastSyncAt.Should().Be(succeeded);
        vm.DocSync.StatusChangedAt.Should().Be(failed);
        // Staleness keys on the success, not the attempt: a run that failed refreshed nothing.
        vm.DocSync.SinceLastSync.Should().Be(Duration.FromHours(50));
        vm.DocSync.IsStale.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetConnectorOverview_ListsActiveAndArchivedMappings_WithCategoryNames()
    {
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        ActiveYearWith((live, "Staff"));
        SeedConnector(map:
        [
            new() { BudgetCategoryId = live, HoldedAccountNumber = 62900101, Tag = "staff", IsActive = true },
            new() { BudgetCategoryId = gone, HoldedAccountNumber = 62900102, Tag = "gone", IsActive = false },
        ]);

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.CategoryMap.Should().HaveCount(2);
        vm.ActiveMappings.Should().Be(1);
        vm.ArchivedMappings.Should().Be(1);
        vm.CategoryMap.Single(m => m.BudgetCategoryId == live).CategoryName.Should().Be("Staff");
        vm.CategoryMap.Single(m => m.BudgetCategoryId == live).GroupName.Should().Be("Operations");
        // A row whose category left the active year keeps its row and loses only its name.
        vm.CategoryMap.Single(m => m.BudgetCategoryId == gone).CategoryName.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConnectorOverview_ListsEveryDoc_MatchedAndUnmatched()
    {
        var cat = Guid.NewGuid();
        ActiveYearWith((cat, "Staff"));
        SeedConnector(docs:
        [
            new()
            {
                HoldedDocId = "d1", DocNumber = "F260001", BudgetCategoryId = cat, Total = 121m,
                MatchStatus = HoldedMatchStatus.Matched, MatchSource = HoldedMatchSource.Account,
                BookedAccountId = "acc-1", TagsJson = "[\"staff\"]", IsApproved = true,
            },
            new()
            {
                HoldedDocId = "d2", DocNumber = "F260002", Total = 50m,
                MatchStatus = HoldedMatchStatus.Unmatched, MatchSource = HoldedMatchSource.None,
                TagsJson = "[]",
            },
        ]);

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.Docs.Should().HaveCount(2);
        vm.MatchedDocs.Should().Be(1);
        vm.UnmatchedDocs.Should().Be(1);

        var matched = vm.Docs.Single(d => string.Equals(d.HoldedDocId, "d1", StringComparison.Ordinal));
        matched.CategoryName.Should().Be("Staff");
        matched.MatchSource.Should().Be(HoldedMatchSource.Account);
        matched.BookedAccountId.Should().Be("acc-1");
        matched.TagsJson.Should().Be("[\"staff\"]");

        vm.Docs.Single(d => string.Equals(d.HoldedDocId, "d2", StringComparison.Ordinal)).CategoryName.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConnectorOverview_NoActiveBudgetYear_StillRenders()
    {
        // The page is the connector's health, not the budget's — it must not go blank between years.
        _budget.GetActiveYearAsync().Returns((BudgetYearDetail?)null);
        SeedConnector(map: [new() { BudgetCategoryId = Guid.NewGuid(), HoldedAccountNumber = 62900101, IsActive = true }]);

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.CategoryMap.Should().ContainSingle().Which.CategoryName.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConnectorOverview_CountsCreditorBindings()
    {
        ActiveYearWith();
        SeedConnector(bindings:
        [
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c1" },
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c2" },
        ]);

        var vm = await MakeService().GetConnectorOverviewAsync(Xunit.TestContext.Current.CancellationToken);

        vm.CreditorBindingCount.Should().Be(2);
    }

    // ─── SEPA payout (nobodies-collective/Humans#1134) ───────────────────────────

    private const string AnaIban = "ES7921000813610123456789";

    /// <summary>One bound member on 40000004 owed €30, with an IBAN on their Holded contact.</summary>
    private Guid SeedPayableCreditor(decimal owed = 30m, string? iban = AnaIban)
    {
        var userId = Guid.NewGuid();
        // The mirror keeps Holded's sign: negative = the organisation owes the member.
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -owed });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Ana Ruiz", SupplierAccountNum = 40000004, Iban = iban },
        });
        return userId;
    }

    private void ConfigureSepa()
    {
        _sepa.CreditorName = "Nobodies Collective";
        _sepa.CreditorIban = "ES9121000418450200051332";
        _sepa.CreditorIdentifier = "G12345678901";
    }

    [HumansFact]
    public void GetSepaPayoutSettings_Unconfigured_NamesEveryMissingKey()
    {
        var settings = MakeService().GetSepaPayoutSettings();

        settings.IsAvailable.Should().BeFalse();
        settings.UnavailableReason.Should().Contain("Sepa:CreditorName")
            .And.Contain("Sepa:CreditorIban").And.Contain("Sepa:CreditorIdentifier");
    }

    [HumansFact]
    public async Task GenerateSepaPayout_Unconfigured_RefusesWithoutTouchingHolded()
    {
        SeedPayableCreditor();

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Xml.Should().BeNull();
        await _repo.DidNotReceive().AddSepaPayoutAsync(
            Arg.Any<SepaPayoutFile>(), Arg.Any<IReadOnlyList<SepaPayoutTransfer>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GenerateSepaPayout_PersistsTheFileAndAuditsEachTransferWithAMaskedIban()
    {
        ConfigureSepa();
        var userId = SeedPayableCreditor();

        var actor = Guid.NewGuid();
        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 12.34m)], actor,
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.FileName.Should().StartWith("nobodies-collective-").And.EndWith(".xml");
        result.FileName.Should().MatchRegex(@"^nobodies-collective-\d{4}-\d{2}-\d{2}-\d{4}-[0-9a-f]{8}\.xml$");

        await _repo.Received(1).AddSepaPayoutAsync(
            Arg.Is<SepaPayoutFile>(f =>
                f.GeneratedByUserId == actor && f.Checksum.Length == 64
                && f.Xml.Contains("CstmrCdtTrfInitn", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyList<SepaPayoutTransfer>>(t =>
                t.Count == 1 && t[0].UserId == userId && t[0].Amount == 12.34m && t[0].Iban == AnaIban),
            Arg.Any<CancellationToken>());

        // The audit trail is where an IBAN would leak if anything but the file carried it raw.
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransfer, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("ES79****789", StringComparison.Ordinal)
                                && !d.Contains(AnaIban, StringComparison.Ordinal)),
            actor, userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task GenerateSepaPayout_TwiceInOneMinute_GivesEachFileItsOwnName()
    {
        // The clock is fixed, so the timestamp is identical across both calls — which is exactly the
        // case that used to collide. The name is the treasurer's handle on a downloaded file.
        ConfigureSepa();
        SeedPayableCreditor(owed: 100m);
        var service = MakeService();

        var first = await service.GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);
        var second = await service.GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.FileName.Should().NotBe(first.FileName);
    }

    [HumansFact]
    public async Task GenerateSepaPayout_MoreThanIsOwed_RefusesTheWholeBatch()
    {
        ConfigureSepa();
        SeedPayableCreditor(owed: 30m);

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 30.01m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("more than the 30.00 EUR owed");
        await _repo.DidNotReceive().AddSepaPayoutAsync(
            Arg.Any<SepaPayoutFile>(), Arg.Any<IReadOnlyList<SepaPayoutTransfer>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GenerateSepaPayout_AboveTheCap_RefusesTheWholeBatch()
    {
        ConfigureSepa();
        SeedPayableCreditor(owed: 300m);

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 60m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cap");
        await _repo.DidNotReceive().AddSepaPayoutAsync(
            Arg.Any<SepaPayoutFile>(), Arg.Any<IReadOnlyList<SepaPayoutTransfer>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GenerateSepaPayout_PartialAmount_IsAccepted()
    {
        ConfigureSepa();
        SeedPayableCreditor(owed: 30m);

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Xml.Should().Contain("<CtrlSum>10.00</CtrlSum>");
    }

    [HumansFact]
    public async Task GenerateSepaPayout_ContactWithNoIban_IsRefused()
    {
        ConfigureSepa();
        SeedPayableCreditor(iban: null);

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no IBAN");
    }

    [HumansFact]
    public async Task ListCreditorAccounts_CarriesTheContactIbanMaskedOnly()
    {
        SeedPayableCreditor();

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.IbanMasked.Should().Be("ES79****789");
    }

    /// <summary>
    /// Holded lets two contacts carry the same 400000xx. The bound member is the second one, so
    /// anything that picks "the account's first contact" picks the wrong person's name and IBAN.
    /// </summary>
    private Guid SeedTwoContactsOnOneAccount()
    {
        var userId = Guid.NewGuid();
        _holded.GetAccountBalancesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [40000004] = -30m });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = userId, HoldedContactId = "c2", SupplierAccountNum = 40000004, Source = CreditorContactSource.Auto },
        });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Wrong Person", SupplierAccountNum = 40000004, Iban = "NL91ABNA0417164300" },
            new() { Id = "c2", Name = "Ana Ruiz", SupplierAccountNum = 40000004, Iban = AnaIban },
        });
        return userId;
    }

    [HumansFact]
    public async Task GenerateSepaPayout_TwoContactsOnOneAccount_PaysTheBoundContact()
    {
        ConfigureSepa();
        SeedTwoContactsOnOneAccount();

        var result = await MakeService().GenerateSepaPayoutAsync(
            [new SepaPayoutSelection(40000004, 10m)], Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Xml.Should().Contain(AnaIban).And.Contain("Ana Ruiz");
        result.Xml.Should().NotContain("Wrong Person").And.NotContain("NL91ABNA0417164300");
    }

    [HumansFact]
    public async Task ListCreditorAccounts_TwoContactsOnOneAccount_ShowsTheBoundContact()
    {
        SeedTwoContactsOnOneAccount();

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("Ana Ruiz");
        row.IbanMasked.Should().Be("ES79****789");
    }
}
