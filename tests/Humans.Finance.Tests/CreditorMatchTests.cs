using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Finance.Services;
using NodaTime;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>A member's booked expense docs must equal the ledger's purchases on their creditor account.</summary>
public class CreditorMatchTests
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 1, 0, 0);

    private static ExpenseReportDto Report(
        decimal total, decimal? cap, ExpenseReportStatus status, params string?[] lineDocIds) => new()
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            HoldedAccountNumber = 0,
            BudgetCategoryId = null,
            BudgetYearId = null,
            Status = status,
            PayeeName = "Ana",
            PayeeIban = "",
            Total = total,
            MaxAmount = cap,
            CreatedAt = At,
            UpdatedAt = At,
            Lines = lineDocIds.Select((doc, i) => new ExpenseLineDto
            {
                Id = Guid.NewGuid(),
                ExpenseReportId = Guid.Empty,
                Description = "x",
                Amount = 1m,
                LineType = default,
                SortOrder = i,
                HoldedDocId = doc,
            }).ToList(),
        };

    [HumansFact]
    public void CappedReport_CountsOnlyBookedDocs_AndMatchesTheLedger()
    {
        // Three lines, the cap left the third unbooked: two docs, 50 payable.
        var reports = new[] { Report(80m, 50m, ExpenseReportStatus.Approved, "d1", "d2", null) };
        var ledger = new (string?, int, decimal, decimal)[]
        {
            ("purchase", 1, 0m, 30m),
            ("purchase", 2, 0m, 20m),
            ("payment", 3, 50m, 0m),
        };

        var m = CreditorMatchService.Match(reports, ledger);

        m.ExpenseDocCount.Should().Be(2);
        m.ExpenseAmount.Should().Be(50m);
        m.PurchaseCount.Should().Be(2);
        m.PurchaseAmount.Should().Be(50m);
        m.PaymentCount.Should().Be(1);
        m.PaymentAmount.Should().Be(50m);
        m.HasWarnings.Should().BeFalse();
    }

    [HumansFact]
    public void MissingPurchase_UnbookedApproval_AndStrayEntry_AreWarnings()
    {
        var reports = new[]
        {
            Report(30m, null, ExpenseReportStatus.Approved, "d1", "d2"),
            Report(10m, null, ExpenseReportStatus.Approved, (string?)null),
        };
        var ledger = new (string?, int, decimal, decimal)[] { ("purchase", 1, 0m, 15m), ("journal", 2, 0m, 5m) };

        var m = CreditorMatchService.Match(reports, ledger);

        m.CountsMatch.Should().BeFalse();
        m.AmountsMatch.Should().BeFalse();
        m.OtherLedgerCount.Should().Be(1);
        m.ApprovedNotBookedCount.Should().Be(1);
        m.Warnings.Should().HaveCount(4);
    }
}
