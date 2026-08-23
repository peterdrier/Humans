using AwesomeAssertions;
using Humans.Expenses.Domain;
using Humans.Expenses.Models;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Tests.Models;

/// <summary>
/// The liability list's worst-first order (nobodies-collective/Humans#1030) is assembled here, not
/// in the service or the repository — it is a display choice, and this is where it is asserted.
/// </summary>
public class CommitmentAwaitingInvoiceViewModelTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 1, 9, 0);

    private static readonly LocalDate Today = new(2026, 6, 1);

    /// <summary>
    /// <paramref name="daysAgo"/> ages the payment by its <c>PaidOn</c> date — when the money
    /// actually left — because that is what the liability sort is defined against. The row's
    /// <c>CreatedAt</c> stays at <see cref="Now"/> so a test that means "old transfer" cannot
    /// pass by accident on the data-entry timestamp instead.
    /// </summary>
    private static VendorCommitmentDto Commitment(string vendor, decimal paid, int daysAgo) =>
        new()
        {
            Id = Guid.NewGuid(),
            VendorName = vendor,
            ExpectedAmount = paid,
            Currency = "EUR",
            Purpose = "Services",
            Status = VendorCommitmentStatus.Paid,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Now,
            UpdatedAt = Now,
            Payments =
            [
                new(Guid.NewGuid(), paid, Today.PlusDays(-daysAgo), null, Guid.NewGuid(), Now),
            ],
        };

    private static CommitmentAwaitingInvoiceViewModel Vm(params VendorCommitmentDto[] commitments) =>
        new() { Commitments = commitments, CategoryNames = new Dictionary<Guid, string>(), Now = Now };

    [HumansFact]
    public void ByLiability_AtTheSameAge_PutsTheLargerAmountFirst()
    {
        var small = Commitment("Alba", 100m, daysAgo: 0);
        var large = Commitment("Cruz Roja", 50_000m, daysAgo: 0);

        Vm(small, large).ByLiability.Select(c => c.Id).Should().ContainInOrder(large.Id, small.Id);
    }

    [HumansFact]
    public void ByLiability_LetsAgeOutweighASlightlyLargerAmount()
    {
        var oldSmaller = Commitment("Alba", 1_000m, daysAgo: 300);
        var freshLarger = Commitment("Repsol", 1_100m, daysAgo: 0);

        Vm(freshLarger, oldSmaller).ByLiability.Select(c => c.Id)
            .Should().ContainInOrder(oldSmaller.Id, freshLarger.Id);
    }

    [HumansFact]
    public void ByLiability_AgesFromTheTransferDate_NotWhenSomeoneTypedItIn()
    {
        // A transfer made months ago, entered today. Ranking it by CreatedAt would score it a
        // same-day liability and bury it under a fresh larger one — the overdue invoice this
        // list exists to surface would be the one you cannot see.
        var backfilled = Commitment("Alba", 1_000m, daysAgo: 300);
        var freshLarger = Commitment("Repsol", 1_100m, daysAgo: 0);
        backfilled.CreatedAt.Should().Be(freshLarger.CreatedAt);

        Vm(freshLarger, backfilled).ByLiability.Select(c => c.Id)
            .Should().ContainInOrder(backfilled.Id, freshLarger.Id);
    }

    [HumansFact]
    public void TotalOutstanding_SumsWhatHasBeenPaidOut()
    {
        Vm(Commitment("Alba", 100m, 0), Commitment("Repsol", 250m, 0))
            .TotalOutstanding.Should().Be(350m);
    }
}
