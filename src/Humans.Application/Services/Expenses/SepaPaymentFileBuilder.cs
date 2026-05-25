using System.Text;
using System.Xml.Linq;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using NodaTime;

namespace Humans.Application.Services.Expenses;

/// <summary>
/// Builds a pain.001.001.09 SEPA Credit Transfer Initiation XML document
/// from a list of approved expense reports. Pure in-memory, no IO.
/// </summary>
public sealed class SepaPaymentFileBuilder : ISepaPaymentFileBuilder
{
    private static readonly XNamespace Ns =
        "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    public string BuildPain001(
        SepaConfig config,
        Instant generatedAt,
        IReadOnlyList<ExpenseReportDto> reports)
    {
        var localDt = generatedAt.InUtc().LocalDateTime;
        var msgId = $"EXP-{localDt:yyyyMMddHHmmss}";
        var nbOfTxs = reports.Count.ToString();
        var ctrlSum = reports.Sum(r => r.Total).ToString("F2",
            System.Globalization.CultureInfo.InvariantCulture);
        var reqExctnDt = localDt.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        var creDtTm = generatedAt.ToString("yyyy-MM-ddTHH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture) + "Z";

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "Document",
                new XElement(Ns + "CstmrCdtTrfInitn",
                    BuildGroupHeader(msgId, creDtTm, nbOfTxs, ctrlSum, config),
                    BuildPaymentInfo(msgId, nbOfTxs, ctrlSum, reqExctnDt, config, reports))));

        // doc.ToString() silently sets OmitXmlDeclaration=true, dropping the
        // XDeclaration. SEPA pain.001 processors at many banks expect the
        // declaration with encoding="utf-8"; Save with a UTF-8 StringWriter
        // preserves both.
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static XElement BuildGroupHeader(
        string msgId, string creDtTm, string nbOfTxs, string ctrlSum,
        SepaConfig config)
    {
        return new XElement(Ns + "GrpHdr",
            new XElement(Ns + "MsgId", msgId),
            new XElement(Ns + "CreDtTm", creDtTm),
            new XElement(Ns + "NbOfTxs", nbOfTxs),
            new XElement(Ns + "CtrlSum", ctrlSum),
            new XElement(Ns + "InitgPty",
                new XElement(Ns + "Nm", config.CreditorName),
                new XElement(Ns + "Id",
                    new XElement(Ns + "OrgId",
                        new XElement(Ns + "Othr",
                            new XElement(Ns + "Id", config.CreditorIdentifier))))));
    }

    private static XElement BuildPaymentInfo(
        string pmtInfId, string nbOfTxs, string ctrlSum, string reqExctnDt,
        SepaConfig config, IReadOnlyList<ExpenseReportDto> reports)
    {
        var pmtInf = new XElement(Ns + "PmtInf",
            new XElement(Ns + "PmtInfId", pmtInfId),
            new XElement(Ns + "PmtMtd", "TRF"),
            new XElement(Ns + "BtchBookg", "false"),
            new XElement(Ns + "NbOfTxs", nbOfTxs),
            new XElement(Ns + "CtrlSum", ctrlSum),
            new XElement(Ns + "PmtTpInf",
                new XElement(Ns + "SvcLvl",
                    new XElement(Ns + "Cd", "SEPA"))),
            new XElement(Ns + "ReqdExctnDt",
                new XElement(Ns + "Dt", reqExctnDt)),
            new XElement(Ns + "Dbtr",
                new XElement(Ns + "Nm", config.CreditorName)),
            new XElement(Ns + "DbtrAcct",
                new XElement(Ns + "Id",
                    new XElement(Ns + "IBAN", config.CreditorIban))),
            new XElement(Ns + "DbtrAgt",
                new XElement(Ns + "FinInstnId",
                    new XElement(Ns + "BICFI", config.CreditorBic))),
            new XElement(Ns + "ChrgBr", config.ChargeBearer));

        foreach (var report in reports)
            pmtInf.Add(BuildCreditTransfer(report));

        return pmtInf;
    }

    private static XElement BuildCreditTransfer(ExpenseReportDto report)
    {
        var endToEndId = report.Id.ToString("N")[..30];
        var instdAmt = report.Total.ToString("F2",
            System.Globalization.CultureInfo.InvariantCulture);
        var remittanceRef = $"Expense {report.Id.ToString("N")[..8]}";

        return new XElement(Ns + "CdtTrfTxInf",
            new XElement(Ns + "PmtId",
                new XElement(Ns + "EndToEndId", endToEndId)),
            new XElement(Ns + "Amt",
                new XElement(Ns + "InstdAmt",
                    new XAttribute("Ccy", "EUR"),
                    instdAmt)),
            new XElement(Ns + "Cdtr",
                new XElement(Ns + "Nm", report.PayeeName)),
            new XElement(Ns + "CdtrAcct",
                new XElement(Ns + "Id",
                    new XElement(Ns + "IBAN", report.PayeeIban))),
            new XElement(Ns + "RmtInf",
                new XElement(Ns + "Ustrd", remittanceRef)));
    }
}
