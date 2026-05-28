using Humans.Application.Configuration;
using Humans.Application.DTOs;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Tickets sync pipeline: upsert vendor orders/attendees, match users by email,
/// compute VAT, enrich Stripe fees, reconcile event participation.
/// </summary>
public sealed class TicketSyncService(
    ITicketRepository ticketRepository,
    ITicketTransferRepository transferRepository,
    ITicketVendorService vendorService,
    IStripeService stripeService,
    IClock clock,
    IOptions<TicketVendorSettings> settings,
    ILogger<TicketSyncService> logger,
    ITicketCacheInvalidator ticketCache,
    IUserServiceRead userServiceRead,
    IUserService userService,
    ICampaignService campaignService,
    IShiftManagementService shiftManagementService) : ITicketSyncService, IUserMerge
{
    private readonly TicketVendorSettings _settings = settings.Value;

    public async Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
        {
            logger.LogDebug("Ticket vendor not configured (missing EventId or API key), skipping sync");
            return new TicketSyncResult(0, 0, 0, 0, 0);
        }

        var eventId = _settings.EventId;

        var syncState = await ticketRepository.GetSyncStateAsync(ct)
            ?? throw new InvalidOperationException("TicketSyncState seed row missing");

        var now = clock.GetCurrentInstant();

        syncState.SyncStatus = TicketSyncStatus.Running;
        syncState.StatusChangedAt = now;
        syncState.VendorEventId = eventId;
        await ticketRepository.PersistSyncStateAsync(syncState, ct);

        try
        {
            var orders = await vendorService.GetOrdersAsync(syncState.LastSyncAt, eventId, ct);
            var tickets = await vendorService.GetIssuedTicketsAsync(syncState.LastSyncAt, eventId, ct);

            var emailLookup = await BuildEmailLookupAsync(ct);

            var existingOrdersByVendorId = await ticketRepository
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

            await ticketRepository.UpsertOrdersAsync(ordersToUpsert, ct);
            var ordersSynced = ordersToUpsert.Count;

            await EnrichOrdersWithStripeDataAsync(ct);

            var orderIdByVendorId = await ticketRepository.GetOrderIdsByVendorIdAsync(ct);
            var existingAttendeesByVendorId = await ticketRepository
                .GetAttendeesByVendorIdsAsync(tickets.Select(t => t.VendorTicketId).ToList(), ct);

            var attendeesToUpsert = new List<TicketAttendee>(tickets.Count);
            var attendeesMatched = 0;
            foreach (var dto in tickets)
            {
                Guid parentOrderId;

                if (dto.VendorOrderId is { Length: > 0 })
                {
                    if (!orderIdByVendorId.TryGetValue(dto.VendorOrderId, out parentOrderId))
                    {
                        logger.LogWarning(
                            "Attendee {VendorTicketId} references unknown order {VendorOrderId}, skipping",
                            dto.VendorTicketId, dto.VendorOrderId);
                        continue;
                    }
                }
                else
                {
                    // API-issued ticket (e.g. transfer reissue) — use existing local parent, else skip.
                    var localExisting = existingAttendeesByVendorId.GetValueOrDefault(dto.VendorTicketId);
                    if (localExisting is null)
                    {
                        logger.LogWarning(
                            "Attendee {VendorTicketId} has null vendor order id and no local row; skipping",
                            dto.VendorTicketId);
                        continue;
                    }
                    parentOrderId = localExisting.TicketOrderId;
                }

                var attendee = BuildAttendeeEntity(dto, eventId, emailLookup, now, parentOrderId,
                    existingAttendeesByVendorId.GetValueOrDefault(dto.VendorTicketId));
                attendeesToUpsert.Add(attendee);
                if (attendee.MatchedUserId.HasValue)
                    attendeesMatched++;
            }

            await ticketRepository.UpsertAttendeesAsync(attendeesToUpsert, ct);
            var attendeesSynced = attendeesToUpsert.Count;

            await ComputeVatForOrdersAsync(ct);

            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            await SyncEventParticipationsAsync(tickets, ct);

            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await ticketRepository.PersistSyncStateAsync(syncState, ct);

            // invalidate ticket caches via seam (§15c)
            ticketCache.InvalidateVendorEventSummary(eventId);
            ticketCache.InvalidateAll();

            var result = new TicketSyncResult(ordersSynced, attendeesSynced,
                ordersMatched, attendeesMatched, codesRedeemed);

            logger.LogInformation(
                "Ticket sync completed: {OrdersSynced} orders, {AttendeesSynced} attendees, " +
                "{OrdersMatched} order matches, {AttendeesMatched} attendee matches, {CodesRedeemed} codes redeemed",
                result.OrdersSynced, result.AttendeesSynced,
                result.OrdersMatched, result.AttendeesMatched, result.CodesRedeemed);

            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null || (int)ex.StatusCode >= 500)
        {
            // Transient: preserve LastSyncAt, retry next run.
            logger.LogWarning(
                "Ticket sync: TicketTailor returned {StatusCode} for event {EventId}, will retry next run",
                (int?)ex.StatusCode, eventId);

            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = clock.GetCurrentInstant();
            await ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

            return new TicketSyncResult(0, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ticket sync failed for event {EventId}", eventId);

            syncState.SyncStatus = TicketSyncStatus.Error;
            syncState.StatusChangedAt = clock.GetCurrentInstant();
            syncState.LastError = ex.Message;
            await ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

            throw;
        }
    }

    public async Task ResetSyncStateForFullResyncAsync()
    {
        await ticketRepository.ResetSyncStateLastSyncAsync();
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await ticketRepository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        await transferRepository.ReassignUserAsync(sourceUserId, targetUserId, ct);

        ticketCache.InvalidateAfterUserMerge(sourceUserId, targetUserId);
    }

    private async Task<Dictionary<string, Guid>> BuildEmailLookupAsync(CancellationToken ct)
    {
        // Verified emails only (#645); gmail/googlemail-normalized; verified-collision = LogError, unmatched.
        var users = await userServiceRead.GetAllUserInfosAsync(ct);
        var entries = users.SelectMany(user => user.UserEmails
            .Where(email => email.IsVerified)
            .Select(email => (email.Email, user.Id)));

        var lookup = new Dictionary<string, Guid>(NormalizingEmailComparer.Instance);
        var grouped = entries.GroupBy(e => e.Email, NormalizingEmailComparer.Instance);
        foreach (var group in grouped)
        {
            var distinctUserIds = group.Select(e => e.Id).Distinct().ToList();

            if (distinctUserIds.Count == 1)
            {
                lookup[group.Key] = distinctUserIds[0];
            }
            else
            {
                logger.LogError(
                    "Email {Email} verified by {Count} users, leaving unmatched",
                    group.Key, distinctUserIds.Count);
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
            // Stripe + VAT enriched later; preserve prior values.
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
        var rows = await ticketRepository.GetOrderDiscountCodesAsync(ct);
        if (rows.Count == 0) return 0;

        var redemptions = rows
            .Select(r => new DiscountCodeRedemption(r.Code, r.PurchasedAt))
            .ToList();

        return await campaignService.MarkGrantsRedeemedAsync(redemptions, ct);
    }

    private async Task EnrichOrdersWithStripeDataAsync(CancellationToken ct)
    {
        if (!stripeService.IsConfigured)
        {
            logger.LogDebug("Stripe not configured, skipping fee enrichment");
            return;
        }

        var ordersToEnrich = await ticketRepository
            .GetOrdersNeedingStripeEnrichmentAsync(ct);

        if (ordersToEnrich.Count == 0) return;

        logger.LogInformation("Enriching {Count} orders with Stripe fee data", ordersToEnrich.Count);

        var updated = new List<TicketOrder>(ordersToEnrich.Count);
        foreach (var order in ordersToEnrich)
        {
            // Defensive: skip placeholder/whitespace PI ids the repo filter should exclude.
            var trimmedPi = order.StripePaymentIntentId?.Trim();
            if (trimmedPi is null or "" or "--")
            {
                continue;
            }

            try
            {
                var details = await stripeService.GetPaymentDetailsAsync(trimmedPi, ct);
                if (details is null) continue;

                order.PaymentMethod = details.PaymentMethod;
                order.PaymentMethodDetail = details.PaymentMethodDetail;
                order.StripeFee = details.StripeFee;
                order.ApplicationFee = details.ApplicationFee;
                updated.Add(order);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Failed to fetch Stripe data for order {OrderId} (PI: {PaymentIntentId}): {Reason}",
                    order.VendorOrderId, trimmedPi, ex.Message);
            }
        }

        await ticketRepository.UpdateOrderStripeEnrichmentAsync(updated, ct);
    }

    /// <summary>
    /// Compute VAT for all orders using VIP split logic.
    /// For each attendee: ticket revenue up to VipThresholdEuros is taxable at VatRate,
    /// any amount above is a VIP donation (VAT-free). Standalone donations are always VAT-free.
    /// We ignore TT's own tax line item because TT incorrectly applies 10% to the full ticket price.
    /// </summary>
    private async Task ComputeVatForOrdersAsync(CancellationToken ct)
    {
        var orders = await ticketRepository.GetAllOrdersWithAttendeesAsync(ct);
        var updates = orders
            .Select(o => (o.Id, VatAmount: ComputeOrderVat(o)))
            .ToList();

        await ticketRepository.UpdateOrderVatAmountsAsync(updates, ct);
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

            // VAT-inclusive: VAT = taxableAmount * rate / (1 + rate)
            var vat = Math.Round(taxableAmount * TicketConstants.VatRate / (1 + TicketConstants.VatRate), 2);
            totalVat += vat;
        }

        return Math.Round(totalVat, 2);
    }

    private static bool IsRevenueAttendee(TicketAttendee attendee) =>
        attendee.Status == TicketAttendeeStatus.Valid || attendee.Status == TicketAttendeeStatus.CheckedIn;

    public async Task<bool> IsInErrorStateAsync(CancellationToken ct = default)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        return syncState?.SyncStatus == TicketSyncStatus.Error;
    }

    public async Task<TicketSyncErrorStatus> GetErrorStatusAsync(CancellationToken ct = default)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        var inError = syncState?.SyncStatus == TicketSyncStatus.Error;
        return new TicketSyncErrorStatus(inError, inError ? syncState?.LastError : null);
    }

    /// <summary>
    /// Derive EventParticipation records from current ticket data.
    /// For each matched user: 1+ valid tickets → Ticketed, any checked-in → Attended.
    /// If user had Ticketed from TicketSync but now has 0 valid tickets → remove record.
    /// <para>
    /// <paramref name="vendorTicketsThisSync"/> carries the per-attendee
    /// <see cref="VendorTicketDto.CheckedInAt"/> when the vendor returned one;
    /// we min across this user's checked-in tickets and pass the result down to
    /// <see cref="IUserService.SetParticipationFromTicketSyncAsync"/>. Older
    /// checked-in tickets not in this delta won't have a timestamp here — that
    /// is the graceful-null fallback (issue nobodies-collective/Humans#736);
    /// the repo's "never overwrite non-null CheckedInAt" rule preserves prior
    /// values.
    /// </para>
    /// </summary>
    private async Task SyncEventParticipationsAsync(
        IReadOnlyList<VendorTicketDto> vendorTicketsThisSync,
        CancellationToken ct)
    {
        var activeEvent = await shiftManagementService.GetActiveAsync();
        if (activeEvent is null || activeEvent.Year == 0)
            return;

        var year = activeEvent.Year;

        var matchedAttendees = await ticketRepository
            .GetMatchedAttendeesForEventAsync(_settings.EventId, ct);

        var userTicketStatuses = matchedAttendees
            .GroupBy(a => a.MatchedUserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.Status).ToList());

        // Per-user min(CheckedInAt) across the vendor tickets we just pulled,
        // joined to matched-attendee rows by VendorTicketId so the user identity
        // matches what the participation loop below uses (MatchedUserId). Using
        // an email lookup here would drop users whose attendee was re-FKed via
        // account-merge or matched through non-email paths.
        // Vendor delta sync only returns *changed* tickets, so this populates
        // timestamps for users whose check-in event was in THIS batch — which
        // is exactly the moment we want to capture the timestamp.
        var matchedUserByVendorTicketId = matchedAttendees
            .ToDictionary(a => a.VendorTicketId, a => a.MatchedUserId, StringComparer.Ordinal);

        var checkedInAtByUserId = vendorTicketsThisSync
            .Where(t => t.CheckedInAt is not null)
            .Select(t => (
                UserId: matchedUserByVendorTicketId.TryGetValue(t.VendorTicketId, out var uid)
                    ? (Guid?)uid
                    : null,
                CheckedInAt: t.CheckedInAt!.Value))
            .Where(x => x.UserId is not null)
            .GroupBy(x => x.UserId!.Value)
            .ToDictionary(g => g.Key, g => g.Min(x => x.CheckedInAt));

        // Current participation state from the in-memory UserInfo cache (zero DB).
        // Diffing against it lets us skip the per-user upsert — and the cache-slice
        // refresh it triggers — for the vast majority of ticket-holders whose status
        // hasn't changed since the last 15-minute sync.
        var current = (await userService.GetAllParticipationsForYearAsync(year, ct))
            .ToDictionary(ep => ep.UserId);

        foreach (var (userId, statuses) in userTicketStatuses)
        {
            var hasCheckedIn = statuses.Any(s => s == TicketAttendeeStatus.CheckedIn);
            var hasValidTicket = statuses.Any(s =>
                s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn);

            current.TryGetValue(userId, out var existing);

            if (hasCheckedIn)
            {
                Instant? checkedInAt = checkedInAtByUserId.TryGetValue(userId, out var ts)
                    ? ts
                    : null;

                // Already Attended with the timestamp settled (or nothing new to
                // record): the upsert would no-op. Attended is permanent and
                // CheckedInAt is write-once.
                if (existing is { Status: ParticipationStatus.Attended }
                    && (checkedInAt is null || existing.CheckedInAt is not null))
                    continue;

                await userService.SetParticipationFromTicketSyncAsync(
                    userId, year, ParticipationStatus.Attended, checkedInAt, ct);
            }
            else if (hasValidTicket)
            {
                // Already Attended (Ticketed can't downgrade it) or already the
                // same Ticketed-from-sync state: the upsert would no-op.
                if (existing is { Status: ParticipationStatus.Attended }
                    or { Status: ParticipationStatus.Ticketed, Source: ParticipationSource.TicketSync })
                    continue;

                await userService.SetParticipationFromTicketSyncAsync(
                    userId, year, ParticipationStatus.Ticketed, checkedInAt: null, ct);
            }
        }

        // Clear stale Ticketed for users who no longer hold a valid ticket.
        var stalePreviousTicketed = current.Values
            .Where(ep => ep.Source == ParticipationSource.TicketSync
                && ep.Status == ParticipationStatus.Ticketed);

        foreach (var ep in stalePreviousTicketed)
        {
            if (!userTicketStatuses.TryGetValue(ep.UserId, out var statuses) ||
                !statuses.Any(s =>
                    s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn))
            {
                await userService.RemoveTicketSyncParticipationAsync(ep.UserId, year, ct);
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
            logger.LogWarning("Unknown payment status '{Status}' from vendor, defaulting to Pending", status);
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
            logger.LogWarning("Unknown attendee status '{Status}' from vendor, defaulting to Void", status);
        }

        return result;
    }
}
