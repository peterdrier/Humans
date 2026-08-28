using System.Globalization;
using System.Xml.Linq;
using AwesomeAssertions;
using Humans.Finance.Services;
using NodaTime;

namespace Humans.Finance.Tests;

/// <summary>
/// The builder is pure, so every rule it enforces is asserted here rather than through a service.
/// The schema check is not a separate test: <see cref="SepaPaymentFileBuilder.Build"/> validates
/// every file it returns against the embedded official XSD, so any test that gets a string back has
/// already proved the file validates.
/// </summary>
public class SepaPaymentFileBuilderTests
{
    private static readonly XNamespace Ns = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 25, 9, 30, 0);

    // Real-shape, check-digit-valid IBANs; none belongs to anyone.
    private const string OrgIban = "ES9121000418450200051332";
    private const string AnaIban = "ES7921000813610123456789";
    private const string BoIban = "NL91ABNA0417164300";

    private static SepaDebtor Org() => new("Nobodies Collective", OrgIban, "BSABESBB", "G12345678901");

    private static SepaPaymentFileRequest Request(
        params SepaTransfer[] transfers) =>
        new(
            MsgId: "M" + Guid.NewGuid().ToString("N"),
            PmtInfId: "P" + Guid.NewGuid().ToString("N"),
            CreatedAt: Now,
            RequestedExecutionDate: new LocalDate(2026, 8, 25),
            Debtor: Org(),
            MaxAmountPerTransfer: 50m,
            Transfers: transfers);

    private static SepaTransfer Ana(decimal amount = 12.34m, string? name = null, int account = 40000004) =>
        new("E" + Guid.NewGuid().ToString("N"), name ?? "Ana Ruiz", AnaIban, amount, account);

    private static SepaTransfer Bo(decimal amount = 20m) =>
        new("E" + Guid.NewGuid().ToString("N"), "Bo Jansen", BoIban, amount, 40000007);

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    // ─── The file itself ────────────────────────────────────────────────────────

    [HumansFact]
    public void Build_SingleTransfer_ProducesTheSabadellShape()
    {
        var doc = Parse(SepaPaymentFileBuilder.Build(Request(Ana())));

        var pmtInf = doc.Root!.Element(Ns + "CstmrCdtTrfInitn")!.Element(Ns + "PmtInf")!;
        pmtInf.Element(Ns + "PmtMtd")!.Value.Should().Be("TRF");
        pmtInf.Element(Ns + "PmtTpInf")!.Element(Ns + "SvcLvl")!.Element(Ns + "Cd")!.Value.Should().Be("SEPA");
        pmtInf.Element(Ns + "ReqdExctnDt")!.Element(Ns + "Dt")!.Value.Should().Be("2026-08-25");
        pmtInf.Element(Ns + "DbtrAcct")!.Descendants(Ns + "IBAN").Single().Value.Should().Be(OrgIban);

        var tx = pmtInf.Element(Ns + "CdtTrfTxInf")!;
        tx.Element(Ns + "Amt")!.Element(Ns + "InstdAmt")!.Attribute("Ccy")!.Value.Should().Be("EUR");
        tx.Element(Ns + "Amt")!.Element(Ns + "InstdAmt")!.Value.Should().Be("12.34");
        tx.Element(Ns + "CdtrAcct")!.Descendants(Ns + "IBAN").Single().Value.Should().Be(AnaIban);
        tx.Element(Ns + "RmtInf")!.Elements(Ns + "Ustrd").Should().ContainSingle();
    }

    [HumansFact]
    public void Build_OmitsEverythingSabadellRejects()
    {
        // Postal addresses, the creditor agent, the charge bearer and any category-purpose code —
        // SALA above all, which routes a reimbursement as payroll.
        var xml = SepaPaymentFileBuilder.Build(Request(Ana(), Bo()));

        var doc = Parse(xml);
        doc.Descendants(Ns + "PstlAdr").Should().BeEmpty();
        doc.Descendants(Ns + "CdtrAgt").Should().BeEmpty();
        doc.Descendants(Ns + "ChrgBr").Should().BeEmpty();
        doc.Descendants(Ns + "CtgyPurp").Should().BeEmpty();
        xml.Should().NotContain("SALA");
    }

    [HumansFact]
    public void Build_MultipleRecipients_CountsAndControlSumsAgreeAtBothLevels()
    {
        var doc = Parse(SepaPaymentFileBuilder.Build(Request(Ana(12.34m), Bo(20m), Ana(0.01m))));

        var init = doc.Root!.Element(Ns + "CstmrCdtTrfInitn")!;
        var transactions = init.Element(Ns + "PmtInf")!.Elements(Ns + "CdtTrfTxInf").ToList();
        var summed = transactions
            .Sum(t => decimal.Parse(t.Descendants(Ns + "InstdAmt").Single().Value,
                CultureInfo.InvariantCulture));

        transactions.Should().HaveCount(3);
        summed.Should().Be(32.35m);
        init.Element(Ns + "GrpHdr")!.Element(Ns + "NbOfTxs")!.Value.Should().Be("3");
        init.Element(Ns + "GrpHdr")!.Element(Ns + "CtrlSum")!.Value.Should().Be("32.35");
        init.Element(Ns + "PmtInf")!.Element(Ns + "NbOfTxs")!.Value.Should().Be("3");
        init.Element(Ns + "PmtInf")!.Element(Ns + "CtrlSum")!.Value.Should().Be("32.35");
    }

    [HumansFact]
    public void Build_PresenterIdComesFromTheRequest_NeverInferred()
    {
        var doc = Parse(SepaPaymentFileBuilder.Build(Request(Ana())));

        doc.Descendants(Ns + "InitgPty").Single()
            .Descendants(Ns + "Othr").Single()
            .Element(Ns + "Id")!.Value.Should().Be("G12345678901");
    }

    [HumansFact]
    public void Build_NoBicConfigured_StillProducesAValidFile()
    {
        var request = Request(Ana()) with { Debtor = Org() with { Bic = null } };

        var doc = Parse(SepaPaymentFileBuilder.Build(request));

        doc.Descendants(Ns + "DbtrAgt").Should().ContainSingle()
            .Which.Descendants(Ns + "BICFI").Should().BeEmpty();
    }

    // ─── Remittance information (nobodies-collective/Humans#1141) ───────────────

    [HumansFact]
    public void Build_RemittanceCarriesEachTransfersOwnAccountAndCreditorName()
    {
        // "<account> - NCA - <name>": the account ties a bank line to a creditor account and the name
        // says who was paid — both per transfer, so no two lines in a batch read identically.
        var doc = Parse(SepaPaymentFileBuilder.Build(Request(Ana(account: 40000004), Bo())));

        var remittances = doc.Descendants(Ns + "Ustrd").Select(u => u.Value).ToList();

        remittances.Should().Equal(
            "40000004 - NCA - Ana Ruiz",
            "40000007 - NCA - Bo Jansen");
    }

    // ─── Character handling ─────────────────────────────────────────────────────

    [HumansFact]
    public void Build_NamesAreFoldedIntoTheSepaSubset()
    {
        var request = Request(Ana(name: "Iñaki Peña-Çelik & Søren <Bø>"));

        var name = Parse(SepaPaymentFileBuilder.Build(request))
            .Descendants(Ns + "Cdtr").Single().Element(Ns + "Nm")!.Value;

        name.Should().Be("Inaki Pena-Celik Soren Bo");
    }

    [HumansFact]
    public void Build_XmlReservedCharactersAreEscaped_NotDropped()
    {
        // The apostrophe is inside the SEPA subset, so it survives the fold and must be escaped on
        // the way out rather than corrupting the document.
        var xml = SepaPaymentFileBuilder.Build(Request(Ana(name: "O'Brien & Co")));

        Parse(xml).Descendants(Ns + "Cdtr").Single().Element(Ns + "Nm")!.Value
            .Should().Be("O'Brien Co");
    }

    [HumansFact]
    public void Build_LongNameIsCappedAtSeventy()
    {
        var request = Request(Ana(name: new string('a', 120)));

        Parse(SepaPaymentFileBuilder.Build(request))
            .Descendants(Ns + "Cdtr").Single().Element(Ns + "Nm")!.Value
            .Should().HaveLength(70);
    }

    // ─── Refusals ───────────────────────────────────────────────────────────────

    [HumansFact]
    public void Build_AmountOverTheCap_RefusesTheWholeFile()
    {
        var act = () => SepaPaymentFileBuilder.Build(Request(Ana(12.34m), Bo(50.01m)));

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*50.00 EUR per-transfer cap*");
    }

    [HumansFact]
    public void Build_AmountAtTheCap_IsAllowed()
    {
        var doc = Parse(SepaPaymentFileBuilder.Build(Request(Bo(50m))));

        doc.Descendants(Ns + "InstdAmt").Single().Value.Should().Be("50.00");
    }

    [HumansFact]
    public void Build_AmountBelowOneCent_IsRefused()
    {
        var act = () => SepaPaymentFileBuilder.Build(Request(Ana(0m)));

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*at least*");
    }

    [HumansFact]
    public void Build_AmountWithMoreThanTwoDecimals_IsRefused()
    {
        var act = () => SepaPaymentFileBuilder.Build(Request(Ana(12.345m)));

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*two decimal places*");
    }

    [HumansFact]
    public void Build_InvalidIban_IsRefusedAndTheMessageMasksIt()
    {
        var bad = new SepaTransfer("E" + Guid.NewGuid().ToString("N"), "Ana Ruiz",
            "ES9121000418450200051333", 10m, 40000004);

        var act = () => SepaPaymentFileBuilder.Build(Request(bad));

        var message = act.Should().Throw<SepaPaymentFileException>().Which.Message;
        message.Should().Contain("ES91****333");
        message.Should().NotContain("ES9121000418450200051333");
    }

    [HumansFact]
    public void Build_DuplicateEndToEndId_IsRefused()
    {
        var id = "E" + Guid.NewGuid().ToString("N");
        var act = () => SepaPaymentFileBuilder.Build(Request(
            new SepaTransfer(id, "Ana Ruiz", AnaIban, 10m, 40000004),
            new SepaTransfer(id, "Bo Jansen", BoIban, 11m, 40000007)));

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*share the end-to-end id*");
    }

    [HumansFact]
    public void Build_IdLongerThanThirtyFive_IsRefused()
    {
        var act = () => SepaPaymentFileBuilder.Build(Request(Ana()) with { MsgId = new string('M', 36) });

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*message id*");
    }

    [HumansFact]
    public void Build_NoTransfers_IsRefused()
    {
        var act = () => SepaPaymentFileBuilder.Build(Request());

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*at least one transfer*");
    }

    [HumansFact]
    public void Build_DebtorNotConfigured_IsRefused()
    {
        var request = Request(Ana()) with { Debtor = new SepaDebtor("", "", null, "") };

        var act = () => SepaPaymentFileBuilder.Build(request);

        act.Should().Throw<SepaPaymentFileException>().WithMessage("*not configured*");
    }
}
