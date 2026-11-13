using System.Security.Cryptography;
using System.Text;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using static Humans.Finance.Services.CreditorReader;
using static Humans.Finance.Services.SepaText;

namespace Humans.Finance.Services;

internal sealed class SepaPayoutGeneration(
    IFinanceRepository repo,
    IHoldedService holded,
    CreditorReader creditorReader,
    IClock clock,
    IAuditLogService audit,
    IOptions<SepaOptions> sepa,
    SepaPayoutEmails payoutEmails,
    ILogger<Service> logger) : IApplicationService
{
    // Persisted in audit_log.entity_type; pinned so a rename cannot silently orphan old rows.
    private const string SepaTransferEntityType = nameof(SepaPayoutTransfer);

    internal SepaPayoutSettings GetSepaPayoutSettings() => sepa.Value.GetPayoutSettings();

    internal async Task<SepaPayoutPreview> PreviewSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, CancellationToken ct = default)
    {
        var (plan, error) = await PlanSepaPayoutAsync(selections, maxPerTransfer, ct);
        return plan is null ? SepaPayoutPreview.Failure(error!) : new SepaPayoutPreview(plan.Lines, null);
    }

    internal async Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, Guid actorUserId,
        CancellationToken ct = default)
    {
        var (plan, error) = await PlanSepaPayoutAsync(selections, maxPerTransfer, ct);
        if (plan is null) return SepaPayoutResult.Failure(error!);
        var (fileId, now, transfers, _, xml) = plan;

        // The stamp is minute-resolution, and the name is what ties a file on the treasurer's disk to its row, so the
        // file id's first 8 hex characters keep two batches in one minute apart.
        var fileName = $"{FileSlug(sepa.Value.CreditorName!)}-"
                     + $"{now.InZone(MadridZone).ToDateTimeUnspecified().ToFileTimestamp()}-"
                     + $"{fileId.ToString("N")[..8]}.xml";

        await repo.AddSepaPayoutAsync(new SepaPayoutFile
        {
            Id = fileId,
            GeneratedAt = now,
            GeneratedByUserId = actorUserId,
            FileName = fileName,
            Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant(),
            Xml = xml,
        }, transfers, ct);

        // After the save: a rolled-back write must not leave a ghost audit row.
        foreach (var t in transfers)
            await audit.LogAsync(
                FinanceAuditActions.SepaPayoutTransfer, SepaTransferEntityType, t.Id,
                $"SEPA payout {Euros(t.Amount)} to {t.IbanMasked} on creditor account "
                + $"{t.SupplierAccountNum} (file {fileName}).",
                actorUserId, t.UserId, AuditSubjectTypes.User);

        // The payout is committed: abandoning the download must not suppress its notifications.
        await payoutEmails.SendPayoutEmailsAsync(transfers);

        return new SepaPayoutResult(fileName, xml, null);
    }

    private sealed record SepaPayoutPlan(
        Guid FileId, Instant Now, IReadOnlyList<SepaPayoutTransfer> Transfers,
        IReadOnlyList<SepaPayoutPreviewLine> Lines, string Xml);

    /// <summary>Everything but the write, so the confirmation page refuses exactly what generation would.</summary>
    private async Task<(SepaPayoutPlan? Plan, string? Error)> PlanSepaPayoutAsync(
        IReadOnlyList<SepaPayoutSelection> selections, decimal maxPerTransfer, CancellationToken ct)
    {
        var settings = GetSepaPayoutSettings();
        if (settings.UnavailableReason is { } unavailable)
            return (null, unavailable);

        if (selections.Count == 0)
            return (null, "No creditor account was selected — nothing to generate.");

        if (selections.GroupBy(s => s.SupplierAccountNum).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            return (null,
                $"Creditor account {duplicate.Key} was selected twice — a payout pays each account once.");

        // The file pays the IBAN on Holded's contact now, not last night's: a fix made in Holded today must not pay the
        // old account. So a payout refreshes the contact mirror first and refuses without it.
        try
        {
            if (!await holded.SyncContactsAsync(ct))
                return (null, "A Holded contact sync is already running, so the payees' IBANs cannot be "
                              + "confirmed — try again in a moment.");
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            return (null,
                $"Holded's contacts could not be refreshed, so the payees' IBANs cannot be confirmed: {ex.Message}");
        }

        var (rows, _, contactsUnavailable) = await creditorReader.ListCreditorAccountsAsync(ct);
        if (contactsUnavailable is not null)
            return (null, contactsUnavailable);
        var byAccount = rows.ToDictionary(r => r.SupplierAccountNum);

        // The unmasked IBAN goes nowhere but the file and the payout record. Keyed by contact id, not account number:
        // two contacts can share one 400000xx, and only the bound one is the member's.
        if (await creditorReader.ContactsByIdAsync(ct) is not { } contactById)
            return (null, ContactListUnavailable);

        var fileId = Guid.NewGuid();
        var transfers = new List<SepaPayoutTransfer>(selections.Count);
        var lines = new List<SepaPayoutPreviewLine>(selections.Count);

        foreach (var s in selections)
        {
            if (!byAccount.TryGetValue(s.SupplierAccountNum, out var row))
                return (null,
                    $"Creditor account {s.SupplierAccountNum} is no longer listed — reload the page and try again.");

            if (row.NotPayableReason is { } notPayable)
                return (null, notPayable);

            if (s.Amount > row.OwedToMember)
                return (null,
                    $"Creditor account {s.SupplierAccountNum}: {Euros(s.Amount)} is more than the "
                    + $"{Euros(row.OwedToMember)} owed.");

            // Never pay a contact or IBAN the mirror lacks.
            if (!contactById.TryGetValue(row.Bindings[0].HoldedContactId, out var contact)
                || string.IsNullOrWhiteSpace(contact.Iban))
                return (null,
                    $"Creditor account {s.SupplierAccountNum} has no IBAN on the Holded contact it is bound to.");

            transfers.Add(new SepaPayoutTransfer
            {
                Id = Guid.NewGuid(),
                FileId = fileId,
                UserId = row.Bindings[0].UserId,
                SupplierAccountNum = s.SupplierAccountNum,
                HoldedContactId = row.Bindings[0].HoldedContactId,
                CreditorName = SepaText.Normalize(contact.Name, SepaPaymentFileBuilder.MaxNameLength),
                Iban = IbanValidator.Normalize(contact.Iban),
                IbanMasked = IbanFormatter.Mask(contact.Iban),
                Amount = s.Amount,
            });
            lines.Add(new SepaPayoutPreviewLine(
                s.SupplierAccountNum, row.Bindings[0].UserId, transfers[^1].CreditorName, transfers[^1].IbanMasked,
                row.OwedToMember, s.Amount));
        }

        var now = clock.GetCurrentInstant();
        var request = new SepaPaymentFileRequest(
            MsgId: "M" + fileId.ToString("N"),
            CreatedAt: now,
            RequestedExecutionDate: now.InZone(MadridZone).Date,
            Debtor: new SepaDebtor(
                sepa.Value.CreditorName!, sepa.Value.CreditorIban!,
                sepa.Value.CreditorBic, sepa.Value.CreditorIdentifier!),
            MaxAmountPerTransfer: maxPerTransfer,
            Transfers: transfers
                .Select(t => new SepaTransfer(t.CreditorName, t.Iban, t.Amount, t.SupplierAccountNum))
                .ToList());

        string xml;
        try
        {
            xml = SepaPaymentFileBuilder.Build(request);
        }
        catch (SepaPaymentFileException ex)
        {
            // The builder masks any IBAN it names, so the message is safe to log and to show.
            logger.LogWarning("SEPA payout generation refused: {Reason}", ex.Message);
            return (null, ex.Message);
        }

        return (new SepaPayoutPlan(fileId, now, transfers, lines, xml), null);
    }

    /// <summary>Safe in a download filename.</summary>
    private static string FileSlug(string name)
    {
        var cleaned = new string(SepaText.Normalize(name, 32).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray());
        var slug = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "sepa" : slug;
    }

}
