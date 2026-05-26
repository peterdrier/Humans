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
        _repo.GetCategoryMapAsync(default).ReturnsForAnyArgs(
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
        var plan = await svc.GetProvisioningPlanAsync(blockStart: 6290010);

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
        _repo.GetCategoryMapAsync(default).ReturnsForAnyArgs(new List<HoldedCategoryMap>());

        // … but Holded already has an account at the first block number.
        _client.ListExpenseAccountsAsync(default).ReturnsForAnyArgs(
            new List<HoldedExpenseAccountDto>
            {
                new() { Id = "acc-x", AccountNum = 6290010, Name = "Existing" },
            });

        var svc = MakeService();
        var plan = await svc.GetProvisioningPlanAsync(blockStart: 6290010);

        var toAdd = plan.Rows.Single(r => string.Equals(r.State, "ToAdd", StringComparison.Ordinal));
        toAdd.ProposedAccountNum.Should().Be(6290011); // 6290010 is taken in Holded → skipped
    }

    // ─── Sync ─────────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Sync_attributes_by_account_then_tag_and_counts()
    {
        var catId = Guid.NewGuid();

        // One active map entry: account "acc-1", tag "comms".
        _repo.GetCategoryMapAsync(default).ReturnsForAnyArgs(
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

        _repo.GetSyncStateAsync(default).ReturnsForAnyArgs(new HoldedSyncState
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

        _client.ListPurchaseDocumentsPageAsync(1, 100, default)
            .ReturnsForAnyArgs(ci =>
                (int)ci[0] == 1 ? (IReadOnlyList<HoldedPurchaseDocListItemDto>)page1 : []);

        IReadOnlyList<HoldedExpenseDoc>? capturedDocs = null;
        await _repo.UpsertDocsAsync(
            Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => capturedDocs = d),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());

        var svc = MakeService();
        var result = await svc.SyncAsync();

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
        _repo.GetCategoryMapAsync(default).ReturnsForAnyArgs(new List<HoldedCategoryMap>());

        _repo.GetSyncStateAsync(default).ReturnsForAnyArgs(new HoldedSyncState
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
        var act = () => svc.SyncAsync();

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
        _client.ListChartOfAccountsAsync(default).ReturnsForAnyArgs(new List<HoldedChartAccountDto>
        {
            new() { Num = 40000001, Name = "Daniela", Balance = -3180m },
            new() { Num = 40000004, Name = "Peter",   Balance = -23m },
            new() { Num = 62900000, Name = "Otros",   Balance = 12m },  // not a creditor acct
        });
        _client.ListPaymentsAsync(default).ReturnsForAnyArgs(new List<HoldedPaymentDto>
        {
            new() { Id = "p1", ContactId = "c1", Amount = 50m, Date = FixedNow, DocumentType = "purchase" },
        });

        IReadOnlyList<HoldedCreditorBalance>? balances = null;
        await _repo.UpsertCreditorBalancesAsync(
            Arg.Do<IReadOnlyList<HoldedCreditorBalance>>(b => balances = b), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        IReadOnlyList<HoldedPayment>? payments = null;
        await _repo.UpsertPaymentsAsync(
            Arg.Do<IReadOnlyList<HoldedPayment>>(p => payments = p), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await MakeService().SyncCreditorDataAsync();

        balances.Should().NotBeNull();
        balances!.Select(b => b.SupplierAccountNum).Should().BeEquivalentTo(new[] { 40000001, 40000004 });
        payments.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetCreditorStatus_computes_owed_and_paid()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(40000001, default).ReturnsForAnyArgs(
            new HoldedCreditorBalance { SupplierAccountNum = 40000001, Balance = -3180m });
        _repo.GetPaymentsByContactAsync("c1", default).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 100m, Date = new LocalDate(2026, 4, 1) },
            new() { HoldedPaymentId = "p2", HoldedContactId = "c1", Amount = 50m,  Date = new LocalDate(2026, 4, 20) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, "c1");

        status.Should().NotBeNull();
        status!.OwedToMember.Should().Be(3180m);
        status.TotalPaid.Should().Be(150m);
        status.LastPaymentDate.Should().Be(new LocalDate(2026, 4, 20));
    }

    [HumansFact]
    public async Task GetCreditorStatus_returns_null_when_nothing_cached()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(default, default).ReturnsForAnyArgs((HoldedCreditorBalance?)null);
        _repo.GetPaymentsByContactAsync(default!, default).ReturnsForAnyArgs(new List<HoldedPayment>());

        var status = await MakeService().GetCreditorStatusAsync(40000099, "c-unknown");

        status.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCreditorStatus_balance_is_null_when_no_balance_row_even_with_payments()
    {
        // Payments cached but the 400000xx balance row is missing (cache gap / unresolved account).
        // Balance must stay null (unknown) — NOT coerced to 0 — so polling never falsely marks Paid.
        _repo.GetCreditorBalanceByAccountNumAsync(default, default).ReturnsForAnyArgs((HoldedCreditorBalance?)null);
        _repo.GetPaymentsByContactAsync("c1", default).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 60m, Date = new LocalDate(2026, 4, 1) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000007, "c1");

        status.Should().NotBeNull();
        status!.Balance.Should().BeNull();
        status.OwedToMember.Should().Be(0m);
        status.TotalPaid.Should().Be(60m);
    }
}
