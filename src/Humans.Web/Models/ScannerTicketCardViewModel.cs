using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>Render model for the /Scanner/Tickets result card. Built in the
/// controller from the cached TicketAttendeeInfo — no new Application surface.</summary>
public sealed record ScannerTicketCardViewModel(
    bool Found,
    string? ScannedBarcode,
    TicketStubInfo? Stub,
    string? TicketTypeName,
    string? TransferredToName,
    Instant? TransferredAt);
