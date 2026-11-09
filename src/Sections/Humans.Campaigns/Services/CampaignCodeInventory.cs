using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services.Dtos;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Campaigns.Services;

internal sealed class CampaignCodeInventory(
    ICampaignRepository repository,
    ITicketDiscountCodes ticketDiscountCodes,
    CampaignReader reader,
    IClock clock,
    IAuditLogService audit,
    ILogger<CampaignService> logger) : IApplicationService
{
    internal async Task<CampaignImportResult> ImportCodesAsync(Guid actorUserId, Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignImportResult(false, ErrorKey: "NotFound");

        var existingCodes = campaign.Codes
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = clock.GetCurrentInstant();
        var imported = 0;
        var skipped = 0;
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;
        var newCodes = new List<CampaignCode>();

        foreach (var code in codes)
        {
            var trimmed = code.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var characterCount = trimmed.EnumerateRunes().Count();
            if (characterCount > CampaignCode.CodeMaxLength)
            {
                logger.LogWarning("Rejected campaign {CampaignId} code import: length {Length} exceeds {Limit}",
                    campaignId, characterCount, CampaignCode.CodeMaxLength);
                return new CampaignImportResult(false, ErrorKey: "CodeTooLong");
            }

            if (existingCodes.Contains(trimmed))
            {
                skipped++;
                continue;
            }

            maxOrder++;
            newCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = trimmed,
                ImportOrder = maxOrder,
                ImportedAt = now
            });
            existingCodes.Add(trimmed);
            imported++;
        }

        await repository.AddCampaignCodesAsync(newCodes, ct);
        await audit.LogAsync(CampaignsAuditActions.CampaignCodesImported, "Campaign", campaignId,
            $"Imported {newCodes.Count} codes.", actorUserId);

        logger.LogInformation(
            "Campaign {CampaignId}: imported {Imported} codes, skipped {Skipped} duplicates",
            campaignId, imported, skipped);
        return new CampaignImportResult(true, imported, skipped);
    }

    [ExternalWrite]
    internal async Task<CampaignGenerateCodesResult> GenerateAndImportDiscountCodesAsync(
        Guid actorUserId,
        Guid campaignId,
        int count,
        string discountType,
        decimal discountValue,
        CancellationToken ct = default)
    {
        var campaign = await reader.GetByIdAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignGenerateCodesResult(false, "NotFound");

        if (campaign.Status != CampaignStatus.Draft)
            return new CampaignGenerateCodesResult(false, "NotDraft");

        if (count <= 0)
            return new CampaignGenerateCodesResult(false, "InvalidCount");

        if (!Enum.TryParse<TicketDiscountKind>(discountType, ignoreCase: true, out var parsedKind) ||
            !Enum.IsDefined(parsedKind))
            return new CampaignGenerateCodesResult(false, "InvalidDiscountType");

        // Through Tickets' contract, never the Base vendor port: Tickets is the only door to ticketing.
        var request = new TicketDiscountCodeRequest(count, parsedKind, discountValue, ExpiresAt: null);
        var codes = await ticketDiscountCodes.GenerateAsync(request, ct);
        var imported = await ImportCodesAsync(actorUserId, campaignId, codes, ct);

        return imported.Success
            ? new CampaignGenerateCodesResult(true, GeneratedCount: imported.Imported)
            : new CampaignGenerateCodesResult(false, imported.ErrorKey);
    }

}
