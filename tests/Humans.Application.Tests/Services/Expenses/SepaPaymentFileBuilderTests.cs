using System.Xml.Linq;
using AwesomeAssertions;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Tests.Services.Expenses;

public sealed class SepaPaymentFileBuilderTests
{
    private static readonly XNamespace Ns =
        "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 14, 30, 0);

    private static readonly SepaConfig Config = new()
    {
        CreditorName = "Nobodies Collective",
        CreditorIban = "ES1234567890123456789012",
        CreditorBic = "CAIXESBB",
        CreditorIdentifier = "A12345678",
        ChargeBearer = "SLEV"
    };

    private readonly SepaPaymentFileBuilder _sut = new();

    private static ExpenseReportDto MakeReport(decimal total, string payeeIban = "ES9999999999999999999999")
    {
        return new ExpenseReportDto
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Approved,
            PayeeName = "Alice Example",
            PayeeIban = payeeIban,
            Total = total,
            CreatedAt = FakeNow,
            UpdatedAt = FakeNow,
            Lines = []
        };
    }

    // ─── Structure ────────────────────────────────────────────────────────────

    [HumansFact]
    public void BuildPain001_RootHasCorrectNameAndNamespace()
    {
        var report = MakeReport(10.00m);
        var xml = _sut.BuildPain001(Config, FakeNow, [report]);

        var doc = XDocument.Parse(xml);
        doc.Root!.Name.LocalName.Should().Be("Document");
        doc.Root.Name.NamespaceName.Should().Be(Ns.NamespaceName);
    }

    [HumansFact]
    public void BuildPain001_MsgIdFormatMatchesInstant()
    {
        var report = MakeReport(10.00m);
        var xml = _sut.BuildPain001(Config, FakeNow, [report]);

        var doc = XDocument.Parse(xml);
        var msgId = doc.Descendants(Ns + "MsgId").First().Value;
        msgId.Should().Be("EXP-20260510143000");
    }

    [HumansFact]
    public void BuildPain001_NbOfTxsMatchesReportCount()
    {
        var reports = new[] { MakeReport(10m), MakeReport(20m), MakeReport(5m) };
        var xml = _sut.BuildPain001(Config, FakeNow, reports);

        var doc = XDocument.Parse(xml);
        // Both GrpHdr and PmtInf contain NbOfTxs — both should match count
        var counts = doc.Descendants(Ns + "NbOfTxs")
            .Select(e => e.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        counts.Should().HaveCount(1);
        counts[0].Should().Be("3");
    }

    [HumansFact]
    public void BuildPain001_CtrlSumEqualsSum()
    {
        var reports = new[] { MakeReport(10.00m), MakeReport(20.50m), MakeReport(5.25m) };
        var xml = _sut.BuildPain001(Config, FakeNow, reports);

        var doc = XDocument.Parse(xml);
        // CtrlSum appears in GrpHdr and PmtInf — both should be the same
        var sums = doc.Descendants(Ns + "CtrlSum")
            .Select(e => e.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        sums.Should().HaveCount(1);
        sums[0].Should().Be("35.75");
    }

    // ─── Per-report credit transfers ─────────────────────────────────────────

    [HumansFact]
    public void BuildPain001_EachReportProducesOneCreditTransfer()
    {
        var reports = new[] { MakeReport(10m), MakeReport(20m), MakeReport(5m) };
        var xml = _sut.BuildPain001(Config, FakeNow, reports);

        var doc = XDocument.Parse(xml);
        doc.Descendants(Ns + "CdtTrfTxInf").Count().Should().Be(3);
    }

    [HumansFact]
    public void BuildPain001_CreditTransferHasCorrectIbanAndAmount()
    {
        const string payeeIban = "ES1111222233334444555566";
        var report = MakeReport(42.50m, payeeIban);
        var xml = _sut.BuildPain001(Config, FakeNow, [report]);

        var doc = XDocument.Parse(xml);
        var tx = doc.Descendants(Ns + "CdtTrfTxInf").Single();

        var cdtrIban = tx.Descendants(Ns + "CdtrAcct")
            .SelectMany(e => e.Descendants(Ns + "IBAN"))
            .Single().Value;
        cdtrIban.Should().Be(payeeIban);

        var instdAmt = tx.Descendants(Ns + "InstdAmt").Single().Value;
        instdAmt.Should().Be("42.50");

        var ccy = tx.Descendants(Ns + "InstdAmt").Single().Attribute("Ccy")!.Value;
        ccy.Should().Be("EUR");
    }

    // ─── Multi-report totals ──────────────────────────────────────────────────

    [HumansFact]
    public void BuildPain001_MultiReport_CtrlSumIs35_75()
    {
        var reports = new[] { MakeReport(10.00m), MakeReport(20.50m), MakeReport(5.25m) };
        var xml = _sut.BuildPain001(Config, FakeNow, reports);

        var doc = XDocument.Parse(xml);
        var ctrlSum = doc.Descendants(Ns + "CtrlSum").First().Value;
        ctrlSum.Should().Be("35.75");
    }

    // ─── Empty list ───────────────────────────────────────────────────────────

    [HumansFact]
    public void BuildPain001_EmptyList_NbOfTxsZeroAndCtrlSumZero()
    {
        var xml = _sut.BuildPain001(Config, FakeNow, []);

        var doc = XDocument.Parse(xml);
        doc.Descendants(Ns + "NbOfTxs").First().Value.Should().Be("0");
        doc.Descendants(Ns + "CtrlSum").First().Value.Should().Be("0.00");
        doc.Descendants(Ns + "CdtTrfTxInf").Should().BeEmpty();
    }
}
