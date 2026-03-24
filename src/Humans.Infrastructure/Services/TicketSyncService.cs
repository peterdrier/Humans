using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
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
    private readonly IClock _clock;
    private readonly TicketVendorSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketSyncService> _logger;

    public TicketSyncService(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IClock clock,
        IOptions<TicketVendorSettings> settings,
        ILogger<TicketSyncService> logger,
        IMemoryCache cache)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _clock = clock;
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Ticket vendor not configured (missing EventId or API key), skipping sync");
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

            // Match discount codes to campaign grants
            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            // Update sync state
            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await _dbContext.SaveChangesAsync(ct);

            // Dashboard event summary is cached separately; refresh it after successful sync.
            _cache.Remove(CacheKeys.TicketEventSummary);

            var result = new TicketSyncResult(ordersSynced, attendeesSynced,
                ordersMatched, attendeesMatched, codesRedeemed);

            _logger.LogInformation(
                "Ticket sync completed: {OrdersSynced} orders, {AttendeesSynced} attendees, " +
                "{OrdersMatched} order matches, {AttendeesMatched} attendee matches, {CodesRedeemed} codes redeemed",
                result.OrdersSynced, result.AttendeesSynced,
                result.OrdersMatched, result.AttendeesMatched, result.CodesRedeemed);

            return result;
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
        // Case-insensitive via OrdinalIgnoreCase dictionary and GroupBy
        // If multiple users share same email, prefer the one where it's the OAuth email
        var userEmails = await _dbContext.Set<UserEmail>()
            .Select(ue => new { ue.Email, ue.UserId, ue.IsOAuth })
            .ToListAsync(ct);

        var lookup = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var grouped = userEmails.GroupBy(e => e.Email, StringComparer.OrdinalIgnoreCase);
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

    private static Guid? LookupUserId(Dictionary<string, Guid> lookup, string? email) =>
        email is not null && lookup.TryGetValue(email, out var userId) ? userId : null;

    private TicketPaymentStatus ParsePaymentStatus(string status)
    {
        var result = status.ToLowerInvariant() switch
        {
            "completed" or "paid" => TicketPaymentStatus.Paid,
            "pending" => TicketPaymentStatus.Pending,
            "refunded" => TicketPaymentStatus.Refunded,
            _ => TicketPaymentStatus.Pending
        };

        if (result == TicketPaymentStatus.Pending &&
            !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unknown payment status '{Status}' from vendor, defaulting to Pending", status);
        }

        return result;
    }

    private static TicketAttendeeStatus ParseAttendeeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "valid" or "active" => TicketAttendeeStatus.Valid,
            "void" or "voided" => TicketAttendeeStatus.Void,
            "checked_in" => TicketAttendeeStatus.CheckedIn,
            _ => TicketAttendeeStatus.Valid
        };
}
