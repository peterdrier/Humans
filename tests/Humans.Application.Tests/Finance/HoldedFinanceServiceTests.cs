using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Application.Services.Finance.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Finance;

public class HoldedFinanceServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 5, 1, 12, 0);

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly FakeClock _clock = new(FixedNow);

    private HoldedFinanceService MakeService() => new(
        _repo,
        _client,
        _budget,
        _clock,
        NullLogger<HoldedFinanceService>.Instance);

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

        _repo.GetSyncStateAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new HoldedSyncState
        {
            Id = 1,
            SyncStatus = HoldedSyncStatus.Idle
        });

        var docDate = Instant.FromUtc(2026, 4, 15, 10, 0);

        // 3 docs: account match, tag match, unmatched.
        var page1 = new List<HoldedPurchaseDocListItemDto>
        {
            new()
            {
                Id = "d1", DocNumber = "F001", ContactName = "Alice", Date = docDate,
                Subtotal = 100, Tax = 21, Total = 121, Currency = "eur",
                Lines = [new HoldedPurchaseLineDto { Amount = 100, AccountId = "acc-1", Tags = [] }],
                Tags = [],
            },
            new()
            {
                Id = "d2", DocNumber = "F002", ContactName = "Bob", Date = docDate,
                Subtotal = 50, Tax = 0, Total = 50, Currency = "eur",
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

        _client.ListPurchaseDocumentsPageAsync(1, 100, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci =>
                (int)ci[0] == 1 ? (IReadOnlyList<HoldedPurchaseDocListItemDto>)page1 : []);

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

        var d2 = capturedDocs.Single(d => string.Equals(d.HoldedDocId, "d2", StringComparison.Ordinal));
        d2.MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        d2.MatchSource.Should().Be(HoldedMatchSource.Tag);
        d2.BudgetCategoryId.Should().Be(catId);

        var d3 = capturedDocs.Single(d => string.Equals(d.HoldedDocId, "d3", StringComparison.Ordinal));
        d3.MatchStatus.Should().Be(HoldedMatchStatus.Unmatched);
        d3.MatchSource.Should().Be(HoldedMatchSource.None);
        d3.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task Sync_sets_error_state_on_exception()
    {
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedCategoryMap>());

        _repo.GetSyncStateAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new HoldedSyncState
        {
            Id = 1,
            SyncStatus = HoldedSyncStatus.Idle
        });

        _client.ListPurchaseDocumentsPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Holded API unavailable"));

        HoldedSyncState? savedState = null;
        await _repo.SaveSyncStateAsync(
            Arg.Do<HoldedSyncState>(s => savedState = s),
            Arg.Any<CancellationToken>());

        var svc = MakeService();
        var act = () => svc.SyncAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The last saved state must be Error.
        savedState.Should().NotBeNull();
        savedState!.SyncStatus.Should().Be(HoldedSyncStatus.Error);
        savedState.LastError.Should().NotBeNullOrEmpty();
    }

    // ─── Creditor data ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task SyncCreditorData_caches_only_400000xx_balances_and_all_payments()
    {
        _client.ListChartOfAccountsAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedChartAccountDto>
        {
            new() { Num = 40000001, Name = "Daniela", Balance = -3180m },
            new() { Num = 40000004, Name = "Peter",   Balance = -23m },
            new() { Num = 62900000, Name = "Otros",   Balance = 12m },  // not a creditor acct
        });
        _client.ListPaymentsAsync(Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedPaymentDto>
        {
            new() { Id = "p1", ContactId = "c1", Amount = 50m, Date = FixedNow, DocumentType = "purchase" },
        });

        IReadOnlyList<HoldedCreditorBalance>? balances = null;
        await _repo.UpsertCreditorBalancesAsync(
            Arg.Do<IReadOnlyList<HoldedCreditorBalance>>(b => balances = b), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        IReadOnlyList<HoldedPayment>? payments = null;
        await _repo.UpsertPaymentsAsync(
            Arg.Do<IReadOnlyList<HoldedPayment>>(p => payments = p), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await MakeService().SyncCreditorDataAsync(Xunit.TestContext.Current.CancellationToken);

        balances.Should().NotBeNull();
        balances!.Select(b => b.SupplierAccountNum).Should().BeEquivalentTo(new[] { 40000001, 40000004 });
        payments.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetCreditorStatus_computes_owed_and_paid()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(40000001, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new HoldedCreditorBalance { SupplierAccountNum = 40000001, Balance = -3180m });
        _repo.GetPaymentsByContactAsync("c1", Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 100m, Date = new LocalDate(2026, 4, 1) },
            new() { HoldedPaymentId = "p2", HoldedContactId = "c1", Amount = 50m,  Date = new LocalDate(2026, 4, 20) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, "c1", Xunit.TestContext.Current.CancellationToken);

        status.Should().NotBeNull();
        status.OwedToMember.Should().Be(3180m);
        status.TotalPaid.Should().Be(150m);
        status.LastPaymentDate.Should().Be(new LocalDate(2026, 4, 20));
    }

    [HumansFact]
    public async Task GetCreditorStatus_returns_null_when_nothing_cached()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs((HoldedCreditorBalance?)null);
        _repo.GetPaymentsByContactAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedPayment>());

        var status = await MakeService().GetCreditorStatusAsync(40000099, "c-unknown", Xunit.TestContext.Current.CancellationToken);

        status.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCreditorStatus_balance_is_null_when_no_balance_row_even_with_payments()
    {
        // Payments cached but the 400000xx balance row is missing (cache gap / unresolved account).
        // Balance must stay null (unknown) — NOT coerced to 0 — so polling never falsely marks Paid.
        _repo.GetCreditorBalanceByAccountNumAsync(default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs((HoldedCreditorBalance?)null);
        _repo.GetPaymentsByContactAsync("c1", Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 60m, Date = new LocalDate(2026, 4, 1) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000007, "c1", Xunit.TestContext.Current.CancellationToken);

        status.Should().NotBeNull();
        status.Balance.Should().BeNull();
        status.OwedToMember.Should().Be(0m);
        status.TotalPaid.Should().Be(60m);
    }

    [HumansFact]
    public async Task GetCreditorStatus_surfaces_individual_payment_rows()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(40000001, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(
            new HoldedCreditorBalance { SupplierAccountNum = 40000001, Balance = -100m });
        _repo.GetPaymentsByContactAsync("c1", Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 100m, Date = new LocalDate(2026, 4, 1), DocumentType = "purchase" },
            new() { HoldedPaymentId = "p2", HoldedContactId = "c1", Amount = 50m,  Date = new LocalDate(2026, 4, 20) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, "c1", Xunit.TestContext.Current.CancellationToken);

        status!.Payments.Should().NotBeNull();
        status.Payments!.Should().HaveCount(2);
        status.Payments!.Should().ContainEquivalentOf(new HoldedPaymentInfo(new LocalDate(2026, 4, 1), 100m, "purchase"));
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
        var userId = Guid.NewGuid();
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HoldedContactId = "m-c",
                SupplierAccountNum = 40000001,
                Source = CreditorContactSource.Manual,
            });
        _client.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("m-c");

        await MakeService().EnsureCreditorContactAsync(
            userId, "Daniela Marquez", null, null, null, null,
            Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertCreditorContactAsync(
            Arg.Is<HoldedCreditorContact>(c => c.Source == CreditorContactSource.Manual),
            FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ListCreditorAccounts_DuplicateAccountBinding_DoesNotThrow()
    {
        _repo.GetCreditorBalancesAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorBalance>
        {
            new() { SupplierAccountNum = 40000004, Name = "Peter D", Balance = -10m },
        });
        // Two members mis-bound to the same account number — only UserId is unique in the DB.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c1", SupplierAccountNum = 40000004, Source = CreditorContactSource.Manual },
            new() { UserId = Guid.NewGuid(), HoldedContactId = "c2", SupplierAccountNum = 40000004, Source = CreditorContactSource.Auto },
        });

        var rows = await MakeService().ListCreditorAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle(r => r.SupplierAccountNum == 40000004);
    }

    [HumansFact]
    public async Task GetCreditorLedger_HoldedUnavailable_ServesCachedBalanceWithoutLines()
    {
        _client.ListDailyLedgerAsync(Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("Holded down"));
        _repo.GetCreditorBalanceByAccountNumAsync(40000004, Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorBalance { SupplierAccountNum = 40000004, Name = "Peter D", Balance = -50m });

        var ledger = await MakeService().GetCreditorLedgerAsync(40000004, Xunit.TestContext.Current.CancellationToken);

        ledger.Should().NotBeNull();
        ledger!.Balance.Should().Be(-50m);
        ledger.OwedToMember.Should().Be(50m);
        ledger.Lines.Should().BeEmpty();
    }
}
