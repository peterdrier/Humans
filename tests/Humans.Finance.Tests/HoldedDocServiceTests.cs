using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Finance.Tests;

public class HoldedDocServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 5, 1, 12, 0);

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IBudgetServiceRead _budget = Substitute.For<IBudgetServiceRead>();
    private readonly FakeClock _clock = new(FixedNow);

    private HoldedDocService MakeService() => new(
        _repo,
        _client,
        _budget,
        _clock,
        NullLogger<HoldedDocService>.Instance);

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

    [HumansFact]
    public async Task Sync_BooksEachDocOnItsMadridDate_NotItsUtcDate()
    {
        // Actuals are filtered on Date.Year, and MapDoc is the only place the Madrid
        // conversion happens. An invoice timestamped in the last hour of 31 December UTC is
        // already 1 January in Madrid, so booking it on the UTC date would move it into the
        // previous budget year and understate January.
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new List<HoldedCategoryMap>());
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new HoldedDocSyncState());
        _client.ListDraftPurchaseIdsAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal));
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((IReadOnlyList<HoldedPurchaseDocListItemDto>)
            [
                // CET (UTC+1): 23:30Z on 31 Dec is 00:30 on 1 Jan in Madrid.
                Doc("rolls-over", Instant.FromUtc(2025, 12, 31, 23, 30)),
                // CEST (UTC+2) in summer, and a mid-day stamp that must not move at all.
                Doc("stays-put", Instant.FromUtc(2026, 7, 15, 10, 0)),
            ]);

        IReadOnlyList<HoldedExpenseDoc>? captured = null;
        await _repo.UpsertDocsAsync(
            Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => captured = d),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());

        await MakeService().SyncAsync(Xunit.TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Single(d => string.Equals(d.HoldedDocId, "rolls-over", StringComparison.Ordinal))
            .Date.Should().Be(new LocalDate(2026, 1, 1));
        captured.Single(d => string.Equals(d.HoldedDocId, "stays-put", StringComparison.Ordinal))
            .Date.Should().Be(new LocalDate(2026, 7, 15));
    }

    /// <summary>An otherwise-uninteresting purchase doc, for tests that care only about its date.</summary>
    private static HoldedPurchaseDocListItemDto Doc(string id, Instant date) =>
        new()
        {
            Id = id,
            DocNumber = id,
            ContactName = "Vendor",
            Date = date,
            Subtotal = 10,
            Tax = 0,
            Total = 10,
            Currency = "eur",
            Lines = [],
            Tags = [],
        };

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

        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((IReadOnlyList<HoldedPurchaseDocListItemDto>)docs1);
        _client.ListDraftPurchaseIdsAsync(Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "d2" });

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

}
