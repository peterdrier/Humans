using Humans.Tickets.Contracts;
using Humans.Calendar.Contracts;
using NodaTime;

namespace Humans.Scanner.Models;

/// <summary>Render model for the /Scanner/Tickets card. The per-person fields are null
/// when the ticket has no matched Human; <see cref="PendingConsents"/> empty means every
/// required document is signed.</summary>
internal sealed record ScannerTicketCardViewModel(
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
