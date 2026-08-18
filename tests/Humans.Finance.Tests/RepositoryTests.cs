using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Finance.Tests;

/// <summary>
/// The repository owns all four Finance tables and had no test. What is pinned here is the part
/// that is not a one-line query: what an upsert keeps, what it overwrites, and what it refuses to
/// overwrite with nothing.
/// </summary>
public class RepositoryTests
{
    private static readonly Instant Created = Instant.FromUtc(2026, 4, 1, 0, 0);
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 1, 12, 0);

    private static (Repository Repo, IDbContextFactory<FinanceDbContext> Factory) Make()
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase($"finance-{Guid.NewGuid():N}")
            .Options;
        var factory = new Factory(options);
        return (new Repository(factory), factory);
    }

    private sealed class Factory(DbContextOptions<FinanceDbContext> options)
        : IDbContextFactory<FinanceDbContext>
    {
        public FinanceDbContext CreateDbContext() => new(options);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // ─── Expense docs ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task UpsertDocs_ReSyncingADoc_RewritesTheFactsAndKeepsCreatedAt()
    {
        var (repo, factory) = Make();
        await repo.UpsertDocsAsync([Doc("h-1", total: 100m, category: null,
            status: HoldedMatchStatus.Unmatched)], Created, Ct);

        var categoryId = Guid.NewGuid();
        await repo.UpsertDocsAsync([Doc("h-1", total: 250m, category: categoryId,
            status: HoldedMatchStatus.Matched, isApproved: true)], Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        var stored = ctx.HoldedExpenseDocs.Single();
        stored.Total.Should().Be(250m);
        stored.BudgetCategoryId.Should().Be(categoryId);
        stored.MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        stored.IsApproved.Should().BeTrue();
        stored.LastSyncedAt.Should().Be(Now);
        stored.UpdatedAt.Should().Be(Now);
        stored.CreatedAt.Should().Be(Created); // the row's own history, never restamped
    }

    [HumansFact]
    public async Task UpsertDocs_KeyedOnTheHoldedIdNotOurGuid_SoAReSyncDoesNotDuplicate()
    {
        var (repo, factory) = Make();
        await repo.UpsertDocsAsync([Doc("h-1")], Created, Ct);
        await repo.UpsertDocsAsync([Doc("h-1"), Doc("h-2")], Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        ctx.HoldedExpenseDocs.Select(d => d.HoldedDocId).Should().BeEquivalentTo(["h-1", "h-2"]);
    }

    [HumansFact]
    public async Task GetUnmatched_ReturnsOnlyUnmatchedDocs_NewestFirst()
    {
        var (repo, _) = Make();
        await repo.UpsertDocsAsync(
        [
            Doc("old", date: new LocalDate(2026, 1, 1)),
            Doc("new", date: new LocalDate(2026, 6, 1)),
            Doc("matched", status: HoldedMatchStatus.Matched, category: Guid.NewGuid()),
        ], Now, Ct);

        var rows = await repo.GetUnmatchedAsync(Ct);

        rows.Select(d => d.HoldedDocId).Should().Equal("new", "old");
    }

    [HumansFact]
    public async Task GetMatchedForYear_IsKeyedOnTheDocsOwnCalendarYear()
    {
        var (repo, _) = Make();
        await repo.UpsertDocsAsync(
        [
            Doc("in", status: HoldedMatchStatus.Matched, category: Guid.NewGuid(),
                date: new LocalDate(2026, 12, 31)),
            Doc("out", status: HoldedMatchStatus.Matched, category: Guid.NewGuid(),
                date: new LocalDate(2027, 1, 1)),
            Doc("unmatched", date: new LocalDate(2026, 5, 1)),
        ], Now, Ct);

        var rows = await repo.GetMatchedForYearAsync(2026, Ct);

        rows.Select(d => d.HoldedDocId).Should().Equal("in");
    }

    // ─── Creditor bindings ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task UpsertCreditorContact_ANullAccountNumberDoesNotEraseAResolvedOne()
    {
        // A push that has not resolved the 400000xx yet still writes the contact id. It must not
        // clear a number an earlier push or an admin already recorded.
        var (repo, factory) = Make();
        var userId = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-1", 40000004, CreditorContactSource.Manual), Created, Ct);

        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-2", null, CreditorContactSource.Auto), Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        var stored = ctx.HoldedCreditorContacts.Single();
        stored.HoldedContactId.Should().Be("contact-2");
        stored.SupplierAccountNum.Should().Be(40000004);
        stored.UpdatedAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task UpsertCreditorContact_IsKeyedByMember_SoAMemberNeverHoldsTwoBindings()
    {
        var (repo, factory) = Make();
        var userId = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-1", 40000004), Created, Ct);
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-2", 40000009), Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        var stored = ctx.HoldedCreditorContacts.Single();
        stored.SupplierAccountNum.Should().Be(40000009);
        stored.CreatedAt.Should().Be(Created); // mutated in place, not replaced
    }

    [HumansFact]
    public async Task DeleteCreditorContact_RemovesOnlyThatMembersRow()
    {
        var (repo, factory) = Make();
        var ana = Guid.NewGuid();
        var bo = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(Binding(ana, "c-a", 40000004), Now, Ct);
        await repo.UpsertCreditorContactAsync(Binding(bo, "c-b", 40000005), Now, Ct);

        (await repo.DeleteCreditorContactAsync(ana, Ct)).Should().BeTrue();
        (await repo.DeleteCreditorContactAsync(ana, Ct)).Should().BeFalse();

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        ctx.HoldedCreditorContacts.Single().UserId.Should().Be(bo);
    }

    // ─── Sync state ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task DocSyncState_IsLazyCreatedOnceAndThenRead()
    {
        var (repo, factory) = Make();

        var first = await repo.GetOrCreateDocSyncStateAsync(Ct);
        first.Id.Should().Be(1);
        first.Status.Should().Be("Idle");

        first.Status = "Running";
        first.LastSyncedDocCount = 7;
        await repo.SaveDocSyncStateAsync(first, Ct);

        var second = await repo.GetOrCreateDocSyncStateAsync(Ct);
        second.Status.Should().Be("Running");
        second.LastSyncedDocCount.Should().Be(7);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        ctx.HoldedDocSyncStates.Should().ContainSingle();
    }

    // ─── Category map ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetCategoryMap_ReturnsArchivedRowsToo_TheServiceDecidesWhatIsActive()
    {
        var (repo, _) = Make();
        await repo.AddCategoryMapAsync(new HoldedCategoryMap
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            HoldedAccountNumber = 6290001,
            HoldedAccountId = "acc-1",
            Tag = "a",
            IsActive = false,
            CreatedAt = Created,
            UpdatedAt = Created,
        }, Ct);

        (await repo.GetCategoryMapAsync(Ct)).Should().ContainSingle();
    }

    // ─── Builders ────────────────────────────────────────────────────────────────

    private static HoldedExpenseDoc Doc(
        string holdedDocId,
        decimal total = 10m,
        Guid? category = null,
        HoldedMatchStatus status = HoldedMatchStatus.Unmatched,
        bool? isApproved = null,
        LocalDate? date = null) => new()
        {
            Id = Guid.NewGuid(),
            HoldedDocId = holdedDocId,
            DocNumber = holdedDocId.ToUpperInvariant(),
            ContactName = "Vendor",
            Date = date ?? new LocalDate(2026, 4, 1),
            Total = total,
            Currency = "eur",
            IsApproved = isApproved,
            BudgetCategoryId = category,
            MatchStatus = status,
            MatchSource = category is null ? HoldedMatchSource.None : HoldedMatchSource.Account,
            CreatedAt = Created,
            UpdatedAt = Created,
        };

    private static HoldedCreditorContact Binding(
        Guid userId, string contactId, int? accountNum,
        CreditorContactSource source = CreditorContactSource.Auto) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = source,
            CreatedAt = Created,
            UpdatedAt = Created,
        };
}
