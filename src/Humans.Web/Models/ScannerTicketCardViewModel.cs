using Humans.Application.DTOs;
using Humans.Application.Interfaces.ICalFeed;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>Render model for the /Scanner/Tickets result card. Built in the
/// controller from the cached TicketAttendeeInfo — no new Application surface.
/// The per-person fields (EE sources, check-in, consents, provide list) are
/// null when the ticket has no matched Human (nobodies-collective/Humans#860);
/// <see cref="PendingConsents"/> empty means every required document is signed.</summary>
public sealed record ScannerTicketCardViewModel(
    bool Found,
    string? ScannedBarcode,
    TicketStubInfo? Stub,
    string? TicketTypeName,
    string? TransferredToName,
    Instant? TransferredAt,
    IReadOnlyList<string>? EarlyEntrySources = null,
    Instant? CheckedInAt = null,
    IReadOnlyList<string>? PendingConsents = null,
    IReadOnlyList<CalendarFeedItem>? ProvideItems = null,
    DateTimeZone? BurnTimeZone = null);
