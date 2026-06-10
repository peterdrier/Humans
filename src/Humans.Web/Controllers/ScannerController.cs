using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>Scanner section — in-browser barcode/QR decoders; no server-side writes. See nobodies-collective/Humans#525.</summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Scanner")]
public class ScannerController(
    ITicketServiceRead tickets,
    IUserServiceRead users,
    IEarlyEntryService earlyEntry,
    IConsentServiceRead consents,
    IICalFeedService calendarFeed,
    IEventServiceRead events,
    IBurnSettingsService burnSettings) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Barcode")]
    public IActionResult Barcode() => View();

    [HttpGet("Tickets")]
    public IActionResult Tickets() => View();

    [HttpGet("Tickets/Card")]
    public async Task<IActionResult> Card(string barcode, CancellationToken ct)
    {
        var code = barcode?.Trim() ?? string.Empty;
        if (code.Length == 0)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, null, null, null, null, null));

        // Gate scope: only the current event's tickets are admissible here, so a
        // barcode from a previous event reads as "not found" rather than a valid card.
        var orders = await tickets.GetTicketOrdersAsync(ct);
        var hit = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));

        if (hit is null)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, code, null, null, null, null));

        // Per-person door context (nobodies-collective/Humans#860) — only when the
        // ticket is matched to a Human; unmatched tickets render the bare card.
        UserEarlyEntry? ee = null;
        Instant? checkedInAt = null;
        IReadOnlyList<string>? pendingConsents = null;
        IReadOnlyList<CalendarFeedItem>? provideItems = null;
        DateTimeZone? burnTz = null;

        if (hit.MatchedUserId is { } userId)
        {
            ee = await earlyEntry.GetForUserAsync(userId, ct);
            pendingConsents = await consents.GetPendingDocumentNamesAsync(userId, ct);

            var burn = await burnSettings.GetActiveAsync(ct);
            burnTz = burn is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
            if (burn is not null)
            {
                var info = await users.GetUserInfoAsync(userId, ct);
                checkedInAt = info?.EventParticipations
                    .FirstOrDefault(p => p.Year == burn.Year)?.CheckedInAt;
            }

            provideItems = await GetProvideItemsAsync(userId, burn, burnTz, ct);
        }

        var stub = new TicketStubInfo(
            AttendeeName: hit.AttendeeName ?? "",
            AttendeeEmail: hit.AttendeeEmail,
            Status: hit.Status,
            HasPendingTransfer: false,
            PendingTransferRequestId: null,
            EarlyEntryDate: ee?.EarliestEntryDate);

        return PartialView("_TicketCard", new ScannerTicketCardViewModel(
            true, code, stub, hit.TicketTypeName, hit.TransferredToName, hit.TransferredAt,
            EarlyEntrySources: ee?.Sources,
            CheckedInAt: checkedInAt,
            PendingConsents: pendingConsents,
            ProvideItems: provideItems,
            BurnTimeZone: burnTz));
    }

    /// <summary>
    /// What this human signed up to provide: shift commitments plus events they
    /// are offering, merged and sorted by start. Shifts reuse the iCal feed's
    /// items (its Events half is favourites — the wrong signal at the door, so
    /// those are filtered out); offered events use the same personal filter as
    /// the profile events card (SubmitterUserId match, non-camp — see #935),
    /// with recurring events expanded into one item per occurrence the same way
    /// the Events feed contributor does.
    /// </summary>
    private async Task<IReadOnlyList<CalendarFeedItem>> GetProvideItemsAsync(
        Guid userId, BurnSettingsInfo? burn, DateTimeZone? tz, CancellationToken ct)
    {
        var shiftItems = (await calendarFeed.GetFeedItemsAsync(userId, ct))
            .Where(i => string.Equals(i.Source, "Shifts", StringComparison.Ordinal));

        var offered = (await events.GetApprovedEventsAsync(null, null, null, null, [], ct))
            .Where(e => e.SubmitterUserId == userId && e.CampId is null)
            .SelectMany(e =>
            {
                // tz non-null implies burn non-null (tz is derived from burn in Card).
                IReadOnlyList<Instant> occurrences = tz is not null
                    ? e.GetOccurrenceInstants(burn!.GateOpeningDate, tz)
                    : [e.StartAt];
                return occurrences.Select(start =>
                {
                    var dateKey = DateFormattingExtensions.IcalBasicDatePattern.Format(start.InZone(tz ?? DateTimeZone.Utc).Date);
                    return new CalendarFeedItem(
                        Uid: $"event-{e.Id}-{dateKey}@humans.nobodies.team",
                        Source: "Events",
                        Summary: e.Title,
                        Description: null,
                        Start: start,
                        End: start.Plus(Duration.FromMinutes(e.DurationMinutes)),
                        Location: e.VenueName ?? e.LocationNote,
                        Url: null);
                });
            });

        return shiftItems.Concat(offered).OrderBy(i => i.Start).ToList();
    }
}
