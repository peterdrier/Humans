using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Application-layer implementation of <see cref="ITicketSyncService"/>.
/// Owns domain-persistence for the Tickets section's sync pipeline: upsert
/// vendor orders and attendees into <c>ticket_orders</c>/<c>ticket_attendees</c>,
/// match buyers and attendees to users by email, compute VAT, enrich with
/// Stripe fee data, and reconcile event participation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split vs Ticket Tailor connector (per PR #545c / umbrella #545):</b>
/// This service never calls the vendor API directly — it delegates to
/// <see cref="ITicketVendorService"/>, whose implementation
/// (<c>TicketTailorService</c> or <c>StubTicketVendorService</c>) stays in
/// Infrastructure as the HTTP/vendor connector. All DB access goes through
/// <see cref="ITicketRepository"/> — this type never imports
/// <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </para>
/// <para>
/// <b>Cross-section calls.</b> <c>event_participations</c> writes route
/// through <see cref="IUserService"/> (per the User section's PR #243 — the
/// User section owns that table). <c>EventSettings</c> reads route through
/// <see cref="IShiftManagementService"/>. <c>CampaignGrants</c> redemption
/// updates route through <see cref="ICampaignService"/>.
/// </para>
/// </remarks>
public sealed class TicketSyncService : ITicketSyncService
{
    private readonly ITicketRepository _ticketRepository;
    private readonly ITicketVendorService _vendorService;
    private readonly IStripeService _stripeService;
    private readonly IClock _clock;
    private readonly TicketVendorSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly IUserService _userService;
    private readonly ICampaignService _campaignService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly ILogger<TicketSyncService> _logger;

    public TicketSyncService(
        ITicketRepository ticketRepository,
        ITicketVendorService vendorService,
        IStripeService stripeService,
        IClock clock,
        IOptions<TicketVendorSettings> settings,
        ILogger<TicketSyncService> logger,
        IMemoryCache cache,
        IUserService userService,
        ICampaignService campaignService,
        IShiftManagementService shiftManagementService)
    {
        _ticketRepository = ticketRepository;
        _vendorService = vendorService;
        _stripeService = stripeService;
        _clock = clock;
        _settings = settings.Value;
        _cache = cache;
        _userService = userService;
        _campaignService = campaignService;
        _shiftManagementService = shiftManagementService;
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

        var syncState = await _ticketRepository.GetSyncStateAsync(ct)
            ?? throw new InvalidOperationException("TicketSyncState seed row missing");

        var now = _clock.GetCurrentInstant();

        syncState.SyncStatus = TicketSyncStatus.Running;
        syncState.StatusChangedAt = now;
        syncState.VendorEventId = eventId;
        await _ticketRepository.PersistSyncStateAsync(syncState, ct);

        try
        {
            var orders = await _vendorService.GetOrdersAsync(syncState.LastSyncAt, eventId, ct);
            var tickets = await _vendorService.GetIssuedTicketsAsync(syncState.LastSyncAt, eventId, ct);

            // Build email → UserId lookup from UserEmails table.
            var emailLookup = await BuildEmailLookupAsync(ct);

            // Build entity list for orders — existing rows keep their Id so the repo
            // upsert recognises them; new rows get a freshly-minted Guid.
            var existingOrdersByVendorId = await _ticketRepository
                .GetOrdersByVendorIdsAsync(orders.Select(o => o.VendorOrderId).ToList(), ct);

            var ordersToUpsert = new List<TicketOrder>(orders.Count);
            var ordersMatched = 0;
            foreach (var dto in orders)
            {
                var order = BuildOrderEntity(dto, eventId, emailLookup, now,
                    existingOrdersByVendorId.GetValueOrDefault(dto.VendorOrderId));
                ordersToUpsert.Add(order);
                if (order.MatchedUserId.HasValue)
                    ordersMatched++;
            }

            await _ticketRepository.UpsertOrdersAsync(ordersToUpsert, ct);
            var ordersSynced = ordersToUpsert.Count;

            // Enrich orders with Stripe fee/payment method data.
            await EnrichOrdersWithStripeDataAsync(ct);

            // Build attendee entities. Parent orders must exist by now (just upserted).
            var orderIdByVendorId = await _ticketRepository.GetOrderIdsByVendorIdAsync(ct);
            var existingAttendeesByVendorId = await _ticketRepository
                .GetAttendeesByVendorIdsAsync(tickets.Select(t => t.VendorTicketId).ToList(), ct);

            var attendeesToUpsert = new List<TicketAttendee>(tickets.Count);
            var attendeesMatched = 0;
            foreach (var dto in tickets)
            {
                if (!orderIdByVendorId.TryGetValue(dto.VendorOrderId, out var parentOrderId))
                {
                    _logger.LogWarning(
                        "Attendee {VendorTicketId} references unknown order {VendorOrderId}, skipping",
                        dto.VendorTicketId, dto.VendorOrderId);
                    continue;
                }

                var attendee = BuildAttendeeEntity(dto, eventId, emailLookup, now, parentOrderId,
                    existingAttendeesByVendorId.GetValueOrDefault(dto.VendorTicketId));
                attendeesToUpsert.Add(attendee);
                if (attendee.MatchedUserId.HasValue)
                    attendeesMatched++;
            }

            await _ticketRepository.UpsertAttendeesAsync(attendeesToUpsert, ct);
            var attendeesSynced = attendeesToUpsert.Count;

            // Compute VAT for all orders using VIP split logic on attendee prices.
            await ComputeVatForOrdersAsync(ct);

            // Match discount codes to campaign grants.
            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            // Sync event participation records.
            await SyncEventParticipationsAsync(ct);

            // Update sync state.
            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await _ticketRepository.PersistSyncStateAsync(syncState, ct);

            // Invalidate all ticket-related caches after successful sync.
            _cache.Remove(CacheKeys.TicketEventSummary(eventId));
            _cache.InvalidateTicketCaches();

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
            await _ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

            return new TicketSyncResult(0, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket sync failed for event {EventId}", eventId);

            syncState.SyncStatus = TicketSyncStatus.Error;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastError = ex.Message;
            await _ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

            throw;
        }
    }

    public async Task ResetSyncStateForFullResyncAsync()
    {
        await _ticketRepository.ResetSyncStateLastSyncAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Guid>> BuildEmailLookupAsync(CancellationToken ct)
    {
        // Match against ALL user emails (OAuth, verified, unverified).
        // Normalize for comparison so gmail/googlemail aliases resolve to the same human.
        // If multiple users share same email, prefer the one where it's the OAuth email.
        var entries = await _ticketRepository.GetAllUserEmailLookupEntriesAsync(ct);

        var lookup = new Dictionary<string, Guid>(NormalizingEmailComparer.Instance);
        var grouped = entries.GroupBy(e => e.Email, NormalizingEmailComparer.Instance);
        foreach (var group in grouped)
        {
            var groupEntries = group.ToList();
            var distinctUserIds = groupEntries.Select(e => e.UserId).Distinct().ToList();

            if (distinctUserIds.Count == 1)
            {
                lookup[group.Key] = distinctUserIds[0];
            }
            else
            {
                // Multiple users share this email — prefer OAuth.
                var oauthEntry = groupEntries.FirstOrDefault(e => e.IsOAuth);
                if (oauthEntry is not null)
                {
                    lookup[group.Key] = oauthEntry.UserId;
                }
                else
                {
                    _logger.LogWarning(
                        "Email {Email} shared by {Count} users with no OAuth owner, leaving unmatched",
                        group.Key, distinctUserIds.Count);
                }
            }
        }

        return lookup;
    }

    private TicketOrder BuildOrderEntity(
        VendorOrderDto dto,
        string eventId,
        Dictionary<string, Guid> emailLookup,
        Instant now,
        TicketOrder? existing)
    {
        var id = existing?.Id ?? Guid.NewGuid();

        // Preserve Stripe-enrichment fields on existing rows (they're filled in
        // by a separate pass and the vendor DTO doesn't carry them).
        return new TicketOrder
        {
            Id = id,
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
            // Stripe enrichment + VAT are computed in later passes; preserve any
            // prior values so the upsert doesn't stomp them back to null/0.
            PaymentMethod = existing?.PaymentMethod,
            PaymentMethodDetail = existing?.PaymentMethodDetail,
            StripeFee = existing?.StripeFee,
            ApplicationFee = existing?.ApplicationFee,
            VatAmount = existing?.VatAmount ?? 0m,
        };
    }

    private TicketAttendee BuildAttendeeEntity(
        VendorTicketDto dto,
        string eventId,
        Dictionary<string, Guid> emailLookup,
        Instant now,
        Guid parentOrderId,
        TicketAttendee? existing)
    {
        var id = existing?.Id ?? Guid.NewGuid();
        var matchedUserId = dto.AttendeeEmail is not null
            ? LookupUserId(emailLookup, dto.AttendeeEmail)
            : null;

        return new TicketAttendee
        {
            Id = id,
            VendorTicketId = dto.VendorTicketId,
            TicketOrderId = parentOrderId,
            AttendeeName = dto.AttendeeName,
            AttendeeEmail = dto.AttendeeEmail,
            TicketTypeName = dto.TicketTypeName,
            Price = dto.Price,
            Status = ParseAttendeeStatus(dto.Status),
            VendorEventId = eventId,
            SyncedAt = now,
            MatchedUserId = matchedUserId,
        };
    }

    private async Task<int> MatchDiscountCodesAsync(CancellationToken ct)
    {
        var rows = await _ticketRepository.GetOrderDiscountCodesAsync(ct);
        if (rows.Count == 0) return 0;

        var redemptions = rows
            .Select(r => new DiscountCodeRedemption(r.Code, r.PurchasedAt))
            .ToList();

        // Delegate to CampaignService — CampaignGrants is owned by the Campaigns section.
        return await _campaignService.MarkGrantsRedeemedAsync(redemptions, ct);
    }

    private async Task EnrichOrdersWithStripeDataAsync(CancellationToken ct)
    {
        if (!_stripeService.IsConfigured)
        {
            _logger.LogDebug("Stripe not configured, skipping fee enrichment");
            return;
        }

        var ordersToEnrich = await _ticketRepository
            .GetOrdersNeedingStripeEnrichmentAsync(ct);

        if (ordersToEnrich.Count == 0) return;

        _logger.LogInformation("Enriching {Count} orders with Stripe fee data", ordersToEnrich.Count);

        var updated = new List<TicketOrder>(ordersToEnrich.Count);
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
                updated.Add(order);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Stripe data for order {OrderId} (PI: {PaymentIntentId})",
                    order.VendorOrderId, order.StripePaymentIntentId);
            }
        }

        await _ticketRepository.UpdateOrderStripeEnrichmentAsync(updated, ct);
    }

    /// <summary>
    /// Compute VAT for all orders using VIP split logic.
    /// For each attendee: ticket revenue up to VipThresholdEuros is taxable at VatRate,
    /// any amount above is a VIP donation (VAT-free). Standalone donations are always VAT-free.
    /// We ignore TT's own tax line item because TT incorrectly applies 10% to the full ticket price.
    /// </summary>
    private async Task ComputeVatForOrdersAsync(CancellationToken ct)
    {
        var orders = await _ticketRepository.GetAllOrdersWithAttendeesAsync(ct);
        var updates = orders
            .Select(o => (o.Id, VatAmount: ComputeOrderVat(o)))
            .ToList();

        await _ticketRepository.UpdateOrderVatAmountsAsync(updates, ct);
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

    public async Task<bool> IsInErrorStateAsync(CancellationToken ct = default)
    {
        var syncState = await _ticketRepository.GetSyncStateAsync(ct);
        return syncState?.SyncStatus == TicketSyncStatus.Error;
    }

    /// <summary>
    /// Derive EventParticipation records from current ticket data.
    /// For each matched user: 1+ valid tickets → Ticketed, any checked-in → Attended.
    /// If user had Ticketed from TicketSync but now has 0 valid tickets → remove record.
    /// </summary>
    private async Task SyncEventParticipationsAsync(CancellationToken ct)
    {
        var activeEvent = await _shiftManagementService.GetActiveAsync();
        if (activeEvent is null || activeEvent.Year == 0)
            return;

        var year = activeEvent.Year;

        // Get matched attendees for the active vendor event only.
        var matchedAttendees = await _ticketRepository
            .GetMatchedAttendeesForEventAsync(_settings.EventId, ct);

        // Group by user.
        var userTicketStatuses = matchedAttendees
            .GroupBy(a => a.MatchedUserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.Status).ToList());

        // Process users with tickets.
        foreach (var (userId, statuses) in userTicketStatuses)
        {
            var hasCheckedIn = statuses.Any(s => s == TicketAttendeeStatus.CheckedIn);
            var hasValidTicket = statuses.Any(s =>
                s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn);

            if (hasCheckedIn)
            {
                await _userService.SetParticipationFromTicketSyncAsync(
                    userId, year, ParticipationStatus.Attended, ct);
            }
            else if (hasValidTicket)
            {
                await _userService.SetParticipationFromTicketSyncAsync(
                    userId, year, ParticipationStatus.Ticketed, ct);
            }
        }

        // Handle users who previously had tickets but no longer do. Pull all
        // participation records for the year and filter to TicketSync+Ticketed
        // in memory — at ~500 users this is cheap and avoids adding a tightly
        // scoped read method to IUserService.
        var allParticipations = await _userService.GetAllParticipationsForYearAsync(year, ct);
        var stalePreviousTicketed = allParticipations
            .Where(ep => ep.Source == ParticipationSource.TicketSync
                && ep.Status == ParticipationStatus.Ticketed);

        foreach (var ep in stalePreviousTicketed)
        {
            if (!userTicketStatuses.TryGetValue(ep.UserId, out var statuses) ||
                !statuses.Any(s =>
                    s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn))
            {
                await _userService.RemoveTicketSyncParticipationAsync(ep.UserId, year, ct);
            }
        }
    }

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
