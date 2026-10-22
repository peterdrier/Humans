using Humans.Campaigns.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Helpers;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Stripe.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Users.Contracts;

namespace Humans.Tickets.Services;

internal sealed class TicketSyncService(
    ITicketRepository ticketRepository,
    ITicketTransferRepository transferRepository,
    ITicketVendorService vendorService,
    IStripeService stripeService,
    IClock clock,
    IOptions<TicketVendorSettings> settings,
    ILogger<TicketSyncService> logger,
    IUserService userService,
    IEventParticipationManagement participations,
    ICampaignWrite campaignService,
    ISettingsService settingsService) : ITicketSyncService
{
    private readonly TicketVendorSettings _settings = settings.Value;

    public async Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
        {
            logger.LogWarning("Ticket vendor not configured (missing EventId or API key), skipping sync");
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
            // A sync finishes or defers to the next run; it is never abandoned mid-pagination because the caller left.
            var orders = await vendorService.GetOrdersAsync(syncState.LastSyncAt, eventId, CancellationToken.None);
            var tickets = await vendorService.GetIssuedTicketsAsync(syncState.LastSyncAt, eventId, CancellationToken.None);
            var checkIns = await vendorService.GetCheckInsAsync(syncState.LastSyncAt, eventId, CancellationToken.None);

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
                    // API-issued ticket (e.g. transfer reissue): use the existing local parent, else skip.
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

            // Standalone pass: a check-in can arrive in a delta whose issued ticket was not itself updated.
            await ticketRepository.ApplyCheckInsAsync(checkIns, ct);

            await ComputeVatForOrdersAsync(ct);

            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            await SyncEventParticipationsAsync(ct);

            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await ticketRepository.PersistSyncStateAsync(syncState, ct);

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
            logger.LogWarning(
                "Ticket sync: TicketTailor returned {StatusCode} for event {EventId}, will retry next run: {Reason}",
                (int?)ex.StatusCode, eventId, ex.Message);

            return await DeferSyncAsync(syncState);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // Transient: an inner TimeoutException is HttpClient's own timeout signal. A genuine cancellation carries
            // none and falls through to the Error handler below.
            logger.LogWarning(
                "Ticket sync: TicketTailor request timed out for event {EventId}, will retry next run",
                eventId);

            return await DeferSyncAsync(syncState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ticket sync failed for event {EventId}", eventId);

            syncState.SyncStatus = TicketSyncStatus.Error;
            syncState.StatusChangedAt = clock.GetCurrentInstant();
            syncState.LastError = ex.Message;
            // The column is varchar(2000); logging and the rethrow retain the full diagnostic.
            const int maxErrorLength = 2000;
            if (syncState.LastError.Length > maxErrorLength)
            {
                var length = maxErrorLength;
                if (char.IsHighSurrogate(syncState.LastError[length - 1]) && char.IsLowSurrogate(syncState.LastError[length]))
                    length--;
                syncState.LastError = syncState.LastError[..length];
            }
            await ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

            throw;
        }
    }

    /// <summary>Leave sync Idle and keep LastSyncAt, so the next run retries.</summary>
    private async Task<TicketSyncResult> DeferSyncAsync(TicketSyncState syncState)
    {
        syncState.SyncStatus = TicketSyncStatus.Idle;
        syncState.StatusChangedAt = clock.GetCurrentInstant();
        await ticketRepository.PersistSyncStateAsync(syncState, CancellationToken.None);

        return new TicketSyncResult(0, 0, 0, 0, 0);
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
    }

    private async Task<Dictionary<string, Guid>> BuildEmailLookupAsync(CancellationToken ct)
    {
        // Verified emails only, gmail/googlemail-normalized; a verified collision is logged and left unmatched.
        var users = await userService.GetAllUserInfosAsync(ct);
        var (lookup, collisions) = VerifiedEmailLookup.Build(users);
        foreach (var (email, userCount) in collisions)
        {
            logger.LogError(
                "Email {Email} verified by {Count} users, leaving unmatched",
                email, userCount);
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

        // Keep Stripe-enrichment fields on existing rows: a separate pass fills them and the vendor DTO lacks them.
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
            PaymentMethod = existing?.PaymentMethod,
            PaymentMethodDetail = existing?.PaymentMethodDetail,
            StripeFee = existing?.StripeFee,
            ApplicationFee = existing?.ApplicationFee,
            FeeTax = existing?.FeeTax,
            WithheldTax = existing?.WithheldTax,
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

        // API-issued tickets (transfer reissues) have no real vendor order, and their listed price drifts to the
        // ticket type's current price. Keep the price snapshotted at reissue so revenue and VAT stay anchored.
        var price = string.IsNullOrEmpty(dto.VendorOrderId) && existing is not null
            ? existing.Price
            : dto.Price;

        return new TicketAttendee
        {
            Id = id,
            VendorTicketId = dto.VendorTicketId,
            TicketOrderId = parentOrderId,
            AttendeeName = dto.AttendeeName,
            AttendeeEmail = dto.AttendeeEmail,
            TicketTypeName = dto.TicketTypeName,
            Price = price,
            Status = ParseAttendeeStatus(dto.Status),
            VendorEventId = eventId,
            SyncedAt = now,
            MatchedUserId = matchedUserId,
            Barcode = dto.Barcode,
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
            // Defensive: the repository filter should already exclude placeholder ids.
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
                order.FeeTax = details.FeeTax;
                order.WithheldTax = details.WithheldTax;
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
    /// The vendor's own tax line is ignored: it applies 10% to the full ticket price.
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
    /// Base = Σ min(Price, VipThreshold) over live attendees, minus the order discount (codes reduce the ticket part,
    /// never the VIP donation), floored at zero. VAT-inclusive, rounded once per order.
    /// </summary>
    internal static decimal ComputeOrderVat(TicketOrder order)
    {
        if (order.PaymentStatus != TicketPaymentStatus.Paid)
            return 0m;

        var ticketBase = order.Attendees
            .Where(a => a.Status is TicketAttendeeStatus.Valid or TicketAttendeeStatus.CheckedIn)
            .Sum(a => Math.Min(a.Price, TicketConstants.VipThresholdEuros));

        var taxableAmount = Math.Max(0m, ticketBase - (order.DiscountAmount ?? 0m));

        return Math.Round(taxableAmount * TicketConstants.VatRate / (1 + TicketConstants.VatRate), 2);
    }

    public async Task<bool> IsInErrorStateAsync(CancellationToken ct = default)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        return syncState?.SyncStatus == TicketSyncStatus.Error;
    }

    /// <summary>
    /// Check-in is read from the persisted <see cref="TicketAttendee.CheckedInAt"/>, not inferred from
    /// <see cref="TicketAttendeeStatus"/>: TicketTailor keeps a scanned ticket "valid".
    /// </summary>
    private async Task SyncEventParticipationsAsync(CancellationToken ct)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
        if (activeEvent is null || activeEvent.Year == 0)
            return;

        var year = activeEvent.Year;

        var matchedAttendees = await ticketRepository
            .GetMatchedAttendeesForEventAsync(_settings.EventId, ct);

        // From the persisted attendee rows, so it's right whatever this sync's delta held and self-heals on a full
        // resync.
        var userTickets = matchedAttendees
            .GroupBy(a => a.MatchedUserId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var statuses = g.Select(a => a.Status).ToList();
                    var scans = g.Where(a => a.CheckedInAt is not null)
                        .Select(a => a.CheckedInAt!.Value)
                        .ToList();
                    Instant? earliestScan = scans.Count > 0 ? scans.Min() : null;
                    return (Statuses: statuses, CheckedInAt: earliestScan);
                });

        // Read once and diffed, to skip the upsert for the many holders whose status hasn't changed.
        var current = (await participations.GetAllParticipationsAsync(ct))
            .Where(ep => ep.Year == year)
            .ToDictionary(ep => ep.UserId);

        foreach (var (userId, info) in userTickets)
        {
            var hasCheckedIn = info.CheckedInAt is not null;
            var hasValidTicket = info.Statuses.Any(s =>
                s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn);

            current.TryGetValue(userId, out var existing);

            if (hasCheckedIn)
            {
                // Attended with the timestamp settled: the upsert would no-op.
                if (existing is { Status: ParticipationStatus.Attended }
                    && existing.CheckedInAt is not null)
                    continue;

                await participations.RecordParticipationAsync(
                    userId, year, ParticipationStatus.Attended, info.CheckedInAt, ct);
            }
            else if (hasValidTicket)
            {
                // Already Attended, or already Ticketed from sync: the upsert would no-op.
                if (existing is { Status: ParticipationStatus.Attended }
                    or { Status: ParticipationStatus.Ticketed, Source: ParticipationSource.TicketSync })
                    continue;

                await participations.RecordParticipationAsync(
                    userId, year, ParticipationStatus.Ticketed, checkedInAt: null, ct);
            }
        }

        // Clear stale Ticketed for users who no longer hold a valid ticket.
        var stalePreviousTicketed = current.Values
            .Where(ep => ep.Source == ParticipationSource.TicketSync
                && ep.Status == ParticipationStatus.Ticketed);

        foreach (var ep in stalePreviousTicketed)
        {
            if (!userTickets.TryGetValue(ep.UserId, out var info) ||
                !info.Statuses.Any(s =>
                    s == TicketAttendeeStatus.Valid || s == TicketAttendeeStatus.CheckedIn))
            {
                await participations.RemoveTicketSyncParticipationAsync(ep.UserId, year, ct);
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
