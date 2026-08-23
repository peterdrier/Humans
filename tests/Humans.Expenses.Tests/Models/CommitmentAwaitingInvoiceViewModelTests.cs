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
            CreatedAt = Now - Duration.FromDays(daysAgo),
            UpdatedAt = Now,
            Payments =
            [
                new(Guid.NewGuid(), paid, new LocalDate(2026, 5, 30), null, Guid.NewGuid(),
                    Now - Duration.FromDays(daysAgo)),
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
    public void TotalOutstanding_SumsWhatHasBeenPaidOut()
    {
        Vm(Commitment("Alba", 100m, 0), Commitment("Repsol", 250m, 0))
            .TotalOutstanding.Should().Be(350m);
    }
}
