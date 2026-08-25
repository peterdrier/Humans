using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>Refuses a whole payout file. The message is admin-facing.</summary>
internal sealed class SepaPaymentFileException : Exception
{
    public SepaPaymentFileException() { }
    public SepaPaymentFileException(string message) : base(message) { }
    public SepaPaymentFileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The organisation's side of the transfer — every field configuration-bound.</summary>
internal sealed record SepaDebtor(string Name, string Iban, string? Bic, string PresenterId);

/// <summary>One recipient. <paramref name="EndToEndId"/> comes from the persisted transfer row, and
/// <paramref name="SupplierAccountNum"/> is the 400000xx the remittance text is prefixed with so the
/// treasurer can tie a bank line to a creditor account without opening the file.</summary>
internal sealed record SepaTransfer(
    string EndToEndId, string CreditorName, string Iban, decimal Amount, int SupplierAccountNum);

/// <summary>Everything the file is built from. Pure data — the builder does no IO.</summary>
internal sealed record SepaPaymentFileRequest(
    string MsgId,
    string PmtInfId,
    Instant CreatedAt,
    LocalDate RequestedExecutionDate,
    SepaDebtor Debtor,
    decimal MaxAmountPerTransfer,
    IReadOnlyList<SepaTransfer> Transfers);

/// <summary>
/// Builds a Norma 34-14 / pain.001.001.09 SEPA Credit Transfer file. Pure: no IO, no clock, no
/// configuration — everything arrives on the request, so the whole thing is unit-testable.
/// Every file it returns has been validated against the official ISO 20022 schema.
/// </summary>
internal static class SepaPaymentFileBuilder
{
    private const string Namespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";
    private const int MaxIdLength = 35;
    private const int MaxRemittanceLength = 140;

    /// <summary>SEPA caps a party name at 70, well below the schema's own 140.</summary>
    public const int MaxNameLength = 70;

    /// <summary>Sabadell shows this to the recipient; one occurrence, one line, prefixed per transfer
    /// with the creditor account number it pays.</summary>
    private const string RemittanceInformation = "Nobodies expense reimbursement";

    public static string Build(SepaPaymentFileRequest request)
    {
        Validate(request);

        var ns = XNamespace.Get(Namespace);
        var transactions = request.Transfers
            .Select(t => new XElement(ns + "CdtTrfTxInf",
                new XElement(ns + "PmtId", new XElement(ns + "EndToEndId", t.EndToEndId)),
                new XElement(ns + "Amt",
                    new XElement(ns + "InstdAmt", new XAttribute("Ccy", "EUR"), Money(t.Amount))),
                new XElement(ns + "Cdtr",
                    new XElement(ns + "Nm", SepaText.Normalize(t.CreditorName, MaxNameLength))),
                new XElement(ns + "CdtrAcct",
                    new XElement(ns + "Id", new XElement(ns + "IBAN", IbanValidator.Normalize(t.Iban)))),
                new XElement(ns + "RmtInf",
                    new XElement(ns + "Ustrd",
                        SepaText.Normalize(
                            $"{t.SupplierAccountNum.ToString(CultureInfo.InvariantCulture)} {RemittanceInformation}",
                            MaxRemittanceLength)))))
            .ToList();

        // Counted and summed off the transaction elements that were just built, not off the request,
        // so the header can never describe a different set than the one being serialized.
        var count = transactions.Count.ToString(CultureInfo.InvariantCulture);
        var controlSum = Money(request.Transfers.Sum(t => t.Amount));

        var debtorAgent = new XElement(ns + "FinInstnId");
        if (!string.IsNullOrWhiteSpace(request.Debtor.Bic))
            debtorAgent.Add(new XElement(ns + "BICFI", request.Debtor.Bic.Trim().ToUpperInvariant()));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "Document",
                new XElement(ns + "CstmrCdtTrfInitn",
                    new XElement(ns + "GrpHdr",
                        new XElement(ns + "MsgId", request.MsgId),
                        new XElement(ns + "CreDtTm", request.CreatedAt.ToDateTimeUtc().ToSepaDateTime()),
                        new XElement(ns + "NbOfTxs", count),
                        new XElement(ns + "CtrlSum", controlSum),
                        new XElement(ns + "InitgPty",
                            new XElement(ns + "Nm", SepaText.Normalize(request.Debtor.Name, MaxNameLength)),
                            new XElement(ns + "Id",
                                new XElement(ns + "OrgId",
                                    new XElement(ns + "Othr",
                                        new XElement(ns + "Id", request.Debtor.PresenterId.Trim())))))),
                    new XElement(ns + "PmtInf",
                        new XElement(ns + "PmtInfId", request.PmtInfId),
                        new XElement(ns + "PmtMtd", "TRF"),
                        new XElement(ns + "NbOfTxs", count),
                        new XElement(ns + "CtrlSum", controlSum),
                        new XElement(ns + "PmtTpInf",
                            new XElement(ns + "SvcLvl", new XElement(ns + "Cd", "SEPA"))),
                        new XElement(ns + "ReqdExctnDt",
                            new XElement(ns + "Dt", request.RequestedExecutionDate.ToInvariantDate())),
                        new XElement(ns + "Dbtr",
                            new XElement(ns + "Nm", SepaText.Normalize(request.Debtor.Name, MaxNameLength))),
                        new XElement(ns + "DbtrAcct",
                            new XElement(ns + "Id",
                                new XElement(ns + "IBAN", IbanValidator.Normalize(request.Debtor.Iban)))),
                        new XElement(ns + "DbtrAgt", debtorAgent),
                        transactions))));

        var xml = Serialize(document);
        SepaSchema.Validate(xml);
        return xml;
    }

    /// <summary>UTF-8, no BOM — the declaration has to say UTF-8 and the bytes have to match it.</summary>
    private static string Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Validate(SepaPaymentFileRequest request)
    {
        if (request.Transfers.Count == 0)
            throw new SepaPaymentFileException("Nothing was selected — a SEPA file needs at least one transfer.");

        RequireId(request.MsgId, "message id");
        RequireId(request.PmtInfId, "payment-information id");

        if (string.IsNullOrWhiteSpace(request.Debtor.Name)
            || string.IsNullOrWhiteSpace(request.Debtor.Iban)
            || string.IsNullOrWhiteSpace(request.Debtor.PresenterId))
            throw new SepaPaymentFileException(
                "SEPA generation is unavailable: the organisation's name, IBAN and presenter id are not configured.");

        if (!IbanValidator.IsValid(request.Debtor.Iban))
            throw new SepaPaymentFileException("The organisation's configured IBAN is not a valid IBAN.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in request.Transfers)
        {
            RequireId(t.EndToEndId, "end-to-end id");
            if (!seen.Add(t.EndToEndId))
                throw new SepaPaymentFileException(
                    $"Two transfers share the end-to-end id {t.EndToEndId} — every payment must be uniquely identifiable.");

            if (!IbanValidator.IsValid(t.Iban))
                throw new SepaPaymentFileException(
                    $"{t.CreditorName} has no valid IBAN ({IbanFormatter.Mask(t.Iban)}) — nothing was generated.");

            if (t.Amount < 0.01m)
                throw new SepaPaymentFileException(
                    $"The amount for {t.CreditorName} must be at least €0.01.");

            if (decimal.Round(t.Amount, 2) != t.Amount)
                throw new SepaPaymentFileException(
                    $"The amount for {t.CreditorName} has more than two decimal places.");

            if (t.Amount > request.MaxAmountPerTransfer)
                throw new SepaPaymentFileException(
                    $"The amount for {t.CreditorName} ({Money(t.Amount)} EUR) is above the "
                    + $"{Money(request.MaxAmountPerTransfer)} EUR per-transfer cap — nothing was generated.");
        }
    }

    private static void RequireId(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength)
            throw new SepaPaymentFileException(
                $"The {what} must be 1–{MaxIdLength} characters (was {value?.Length ?? 0}).");
    }

    private static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);
}
