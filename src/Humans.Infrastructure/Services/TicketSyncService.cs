using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class TicketSyncService : ITicketSyncService
{
    private readonly HumansDbContext _dbContext;
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly IClock _clock;
    private readonly TicketVendorSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketSyncService> _logger;

    public TicketSyncService(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IStripeService stripeService,
        IClock clock,
        IOptions<TicketVendorSettings> settings,
        ILogger<TicketSyncService> logger,
        IMemoryCache cache)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _stripeService = stripeService;
        _clock = clock;
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogDebug("Ticket vendor not configured (missing EventId or API key), skipping sync");
            return new TicketSyncResult(0, 0, 0, 0, 0);
        }

        var eventId = _settings.EventId;

        var syncState = await _dbContext.TicketSyncStates.FindAsync([1], ct)
            ?? throw new InvalidOperationException("TicketSyncState seed row missing");

        var now = _clock.GetCurrentInstant();

        syncState.SyncStatus = TicketSyncStatus.Running;
        syncState.StatusChangedAt = now;
        syncState.VendorEventId = eventId;
        await _dbContext.SaveChangesAsync(ct);

        try
        {
            var orders = await _vendorService.GetOrdersAsync(syncState.LastSyncAt, eventId, ct);
            var tickets = await _vendorService.GetIssuedTicketsAsync(syncState.LastSyncAt, eventId, ct);

            // Build email → UserId lookup from UserEmails table
            var emailLookup = await BuildEmailLookupAsync(ct);

            var ordersSynced = 0;
            var attendeesSynced = 0;
            var ordersMatched = 0;
            var attendeesMatched = 0;

            foreach (var orderDto in orders)
            {
                var order = await UpsertOrderAsync(orderDto, eventId, emailLookup, now, ct);
                ordersSynced++;
                if (order.MatchedUserId.HasValue)
                    ordersMatched++;
            }

            // IMPORTANT: Save orders before processing attendees so that
            // UpsertAttendeeAsync can find parent orders via DB query.
            // Without this, newly added orders are only in the Change Tracker
            // and FirstOrDefaultAsync won't see them.
            await _dbContext.SaveChangesAsync(ct);

            // Enrich orders with Stripe fee/payment method data
            await EnrichOrdersWithStripeDataAsync(ct);

            // Sync all tickets (not grouped by order — we match by VendorTicketId)
            foreach (var ticketDto in tickets)
            {
                var attendee = await UpsertAttendeeAsync(ticketDto, eventId, emailLookup, now, ct);
                if (attendee is null) continue; // Skipped — parent order not found
                attendeesSynced++;
                if (attendee.MatchedUserId.HasValue)
                    attendeesMatched++;
            }

            await _dbContext.SaveChangesAsync(ct);

            // Compute VAT for all orders using VIP split logic on attendee prices
            await ComputeVatForOrdersAsync(ct);
            await _dbContext.SaveChangesAsync(ct);

            // Match discount codes to campaign grants
            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            // Update sync state
            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await _dbContext.SaveChangesAsync(ct);

            // Dashboard event summary is cached separately; refresh it after successful sync.
            _cache.Remove(CacheKeys.TicketEventSummary(eventId));

            var result = new TicketSyncResult(ordersSynced, attendeesSynced,
                ordersMatched, attendeesMatched, codesRedeemed);

            _logger.LogInformation(
                "Ticket sync completed: {OrdersSynced} orders, {AttendeesSynced} attendees, " +
                "{OrdersMatched} order matches, {AttendeesMatched} attendee matches, {CodesRedeemed} codes redeemed",
                result.OrdersSynced, result.AttendeesSynced,
                result.OrdersMatched, result.AttendeesMatched, result.CodesRedeemed);

            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null || (int)ex.StatusCode >= 500)
        {
            // Transient HTTP errors (network failures, 5xx responses) — expected.
            // Log concisely (no stack trace), preserve LastSyncAt, retry next run.
            _logger.LogWarning(
                "Ticket sync: TicketTailor returned {StatusCode} for event {EventId}, will retry next run",
                (int?)ex.StatusCode, eventId);

            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync(CancellationToken.None);

            return new TicketSyncResult(0, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket sync failed for event {EventId}", eventId);

            syncState.SyncStatus = TicketSyncStatus.Error;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastError = ex.Message;
            await _dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
    }

    private async Task<Dictionary<string, Guid>> BuildEmailLookupAsync(CancellationToken ct)
    {
        // Match against ALL user emails (OAuth, verified, unverified)
        // Normalize for comparison so gmail/googlemail aliases resolve to the same human.
        // If multiple users share same email, prefer the one where it's the OAuth email
        var userEmails = await _dbContext.Set<UserEmail>()
            .Select(ue => new { ue.Email, ue.UserId, ue.IsOAuth })
            .ToListAsync(ct);

        var lookup = new Dictionary<string, Guid>(NormalizingEmailComparer.Instance);
        var grouped = userEmails.GroupBy(e => e.Email, NormalizingEmailComparer.Instance);
        foreach (var group in grouped)
        {
            var entries = group.ToList();
            var distinctUserIds = entries.Select(e => e.UserId).Distinct().ToList();

            if (distinctUserIds.Count == 1)
            {
                lookup[group.Key] = distinctUserIds[0];
            }
            else
            {
                // Multiple users share this email — prefer OAuth
                var oauthEntry = entries.FirstOrDefault(e => e.IsOAuth);
                if (oauthEntry is not null)
                {
                    lookup[group.Key] = oauthEntry.UserId;
                }
                else
                {
                    _logger.LogWarning("Email {Email} shared by {Count} users with no OAuth owner, leaving unmatched",
                        group.Key, distinctUserIds.Count);
                }
            }
        }

        return lookup;
    }

    private async Task<TicketOrder> UpsertOrderAsync(
        VendorOrderDto dto, string eventId,
        Dictionary<string, Guid> emailLookup, Instant now,
        CancellationToken ct)
    {
        var existing = await _dbContext.TicketOrders
            .FirstOrDefaultAsync(o => o.VendorOrderId == dto.VendorOrderId, ct);

        if (existing is not null)
        {
            existing.BuyerName = dto.BuyerName;
            existing.BuyerEmail = dto.BuyerEmail;
            existing.TotalAmount = dto.TotalAmount;
            existing.Currency = dto.Currency;
            existing.DiscountCode = dto.DiscountCode;
            existing.PaymentStatus = ParsePaymentStatus(dto.PaymentStatus);
            existing.VendorDashboardUrl = dto.VendorDashboardUrl;
            existing.SyncedAt = now;
            existing.MatchedUserId = LookupUserId(emailLookup, dto.BuyerEmail);
            existing.StripePaymentIntentId = dto.StripePaymentIntentId;
            existing.DiscountAmount = dto.DiscountAmount;
            existing.DonationAmount = dto.DonationAmount;
            return existing;
        }

        var order = new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = dto.VendorOrderId,
            BuyerName = dto.BuyerName,
            BuyerEmail = dto.BuyerEmail,
            TotalAmount = dto.TotalAmount,
            Currency = dto.Currency,
            DiscountCode = dto.DiscountCode,
            PaymentStatus = ParsePaymentStatus(dto.PaymentStatus),
            VendorEventId = eventId,
            VendorDashboardUrl = dto.VendorDashboardUrl,
            PurchasedAt = dto.PurchasedAt,
            SyncedAt = now,
            MatchedUserId = LookupUserId(emailLookup, dto.BuyerEmail),
            StripePaymentIntentId = dto.StripePaymentIntentId,
            DiscountAmount = dto.DiscountAmount,
            DonationAmount = dto.DonationAmount,
        };

        _dbContext.TicketOrders.Add(order);
        return order;
    }

    private async Task<TicketAttendee?> UpsertAttendeeAsync(
        VendorTicketDto dto, string eventId,
        Dictionary<string, Guid> emailLookup, Instant now,
        CancellationToken ct)
    {
        var existing = await _dbContext.TicketAttendees
            .FirstOrDefaultAsync(a => a.VendorTicketId == dto.VendorTicketId, ct);

        Guid? matchedUserId = dto.AttendeeEmail is not null
            ? LookupUserId(emailLookup, dto.AttendeeEmail)
            : null;

        if (existing is not null)
        {
            existing.AttendeeName = dto.AttendeeName;
            existing.AttendeeEmail = dto.AttendeeEmail;
            existing.TicketTypeName = dto.TicketTypeName;
            existing.Price = dto.Price;
            existing.Status = ParseAttendeeStatus(dto.Status);
            existing.SyncedAt = now;
            existing.MatchedUserId = matchedUserId;
            return existing;
        }

        // Resolve parent order FK via VendorOrderId from the ticket DTO
        var parentOrder = await _dbContext.TicketOrders
            .FirstOrDefaultAsync(o => o.VendorOrderId == dto.VendorOrderId, ct);

        if (parentOrder is null)
        {
            _logger.LogWarning("Attendee {VendorTicketId} references unknown order {VendorOrderId}, skipping",
                dto.VendorTicketId, dto.VendorOrderId);
            return null;
        }

        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = dto.VendorTicketId,
            TicketOrderId = parentOrder.Id,
            AttendeeName = dto.AttendeeName,
            AttendeeEmail = dto.AttendeeEmail,
            TicketTypeName = dto.TicketTypeName,
            Price = dto.Price,
            Status = ParseAttendeeStatus(dto.Status),
            VendorEventId = eventId,
            SyncedAt = now,
            MatchedUserId = matchedUserId,
        };

        _dbContext.TicketAttendees.Add(attendee);
        return attendee;
    }

    private async Task<int> MatchDiscountCodesAsync(CancellationToken ct)
    {
        var ordersWithCodes = await _dbContext.TicketOrders
            .Where(o => o.DiscountCode != null)
            .Select(o => new { o.DiscountCode, o.PurchasedAt })
            .ToListAsync(ct);

        if (ordersWithCodes.Count == 0) return 0;

        var codeStrings = new HashSet<string>(
            ordersWithCodes.Select(o => o.DiscountCode!),
            StringComparer.OrdinalIgnoreCase);

        // Match codes with ordinal ignore-case semantics so database collation/casing rules
        // do not cause valid imported codes to be skipped.
        var unredeemed = (await _dbContext.Set<CampaignGrant>()
            .Include(g => g.Code)
            .Include(g => g.Campaign)
            .Where(g => g.Code != null
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed)
                && g.RedeemedAt == null)
            .ToListAsync(ct))
            .Where(g => codeStrings.Contains(g.Code!.Code))
            .ToList();

        var codesRedeemed = 0;
        foreach (var order in ordersWithCodes)
        {
            if (order.DiscountCode is null) continue;

            var grant = unredeemed
                .Where(g => string.Equals(g.Code!.Code, order.DiscountCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Campaign.CreatedAt)
                .FirstOrDefault();

            if (grant is not null)
            {
                grant.RedeemedAt = order.PurchasedAt;
                unredeemed.Remove(grant);
                codesRedeemed++;
            }
        }

        if (codesRedeemed > 0)
            await _dbContext.SaveChangesAsync(ct);

        return codesRedeemed;
    }

    private async Task EnrichOrdersWithStripeDataAsync(CancellationToken ct)
    {
        if (!_stripeService.IsConfigured)
        {
            _logger.LogDebug("Stripe not configured, skipping fee enrichment");
            return;
        }

        // Find orders that have a Stripe PI but no fee data yet
        var ordersToEnrich = await _dbContext.TicketOrders
            .Where(o => o.StripePaymentIntentId != null && o.StripeFee == null)
            .ToListAsync(ct);

        if (ordersToEnrich.Count == 0) return;

        _logger.LogInformation("Enriching {Count} orders with Stripe fee data", ordersToEnrich.Count);

        foreach (var order in ordersToEnrich)
        {
            try
            {
                var details = await _stripeService.GetPaymentDetailsAsync(order.StripePaymentIntentId!, ct);
                if (details is null) continue;

                order.PaymentMethod = details.PaymentMethod;
                order.PaymentMethodDetail = details.PaymentMethodDetail;
                order.StripeFee = details.StripeFee;
                order.ApplicationFee = details.ApplicationFee;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Stripe data for order {OrderId} (PI: {PaymentIntentId})",
                    order.VendorOrderId, order.StripePaymentIntentId);
            }
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Compute VAT for all orders using VIP split logic.
    /// For each attendee: ticket revenue up to VipThresholdEuros is taxable at VatRate,
    /// any amount above is a VIP donation (VAT-free). Standalone donations are always VAT-free.
    /// We ignore TT's own tax line item because TT incorrectly applies 10% to the full ticket price.
    /// </summary>
    private async Task ComputeVatForOrdersAsync(CancellationToken ct)
    {
        var orders = await _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .ToListAsync(ct);

        foreach (var order in orders)
        {
            order.VatAmount = ComputeOrderVat(order);
        }
    }

    /// <summary>
    /// Compute VAT for a single order based on its attendees' prices.
    /// For each attendee: the taxable portion is min(Price, VipThreshold).
    /// VAT = taxable / (1 + VatRate) * VatRate (VAT-inclusive calculation).
    /// VIP premiums (price above threshold) and standalone donations are VAT-free.
    /// </summary>
    internal static decimal ComputeOrderVat(TicketOrder order)
    {
        if (order.PaymentStatus != TicketPaymentStatus.Paid)
            return 0m;

        var totalVat = 0m;

        foreach (var attendee in order.Attendees)
        {
            if (!IsRevenueAttendee(attendee))
                continue;

            var taxableAmount = Math.Min(attendee.Price, TicketConstants.VipThresholdEuros);

            // VAT is inclusive in the taxable ticket price:
            // taxableAmount = net + VAT, where VAT = net * rate
            // So: VAT = taxableAmount * rate / (1 + rate)
            var vat = Math.Round(taxableAmount * TicketConstants.VatRate / (1 + TicketConstants.VatRate), 2);
            totalVat += vat;
        }

        return Math.Round(totalVat, 2);
    }

    private static bool IsRevenueAttendee(TicketAttendee attendee) =>
        attendee.Status == TicketAttendeeStatus.Valid || attendee.Status == TicketAttendeeStatus.CheckedIn;

    private static Guid? LookupUserId(Dictionary<string, Guid> lookup, string? email) =>
        email is not null && lookup.TryGetValue(EmailNormalization.NormalizeForComparison(email), out var userId)
            ? userId
            : null;

    private TicketPaymentStatus ParsePaymentStatus(string status)
    {
        var result = status.ToLowerInvariant() switch
        {
            "completed" or "paid" => TicketPaymentStatus.Paid,
            "pending" => TicketPaymentStatus.Pending,
            "refunded" => TicketPaymentStatus.Refunded,
            "cancelled" => TicketPaymentStatus.Cancelled,
            _ => TicketPaymentStatus.Pending
        };

        if (result == TicketPaymentStatus.Pending &&
            !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unknown payment status '{Status}' from vendor, defaulting to Pending", status);
        }

        return result;
    }

    private TicketAttendeeStatus ParseAttendeeStatus(string status)
    {
        var result = status.ToLowerInvariant() switch
        {
            "valid" or "active" => TicketAttendeeStatus.Valid,
            "void" or "voided" => TicketAttendeeStatus.Void,
            "checked_in" => TicketAttendeeStatus.CheckedIn,
            _ => TicketAttendeeStatus.Void
        };

        if (result == TicketAttendeeStatus.Void &&
            !string.Equals(status, "void", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "voided", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unknown attendee status '{Status}' from vendor, defaulting to Void", status);
        }

        return result;
    }
}
