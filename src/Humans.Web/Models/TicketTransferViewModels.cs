using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using NodaTime;

namespace Humans.Web.Models;

public sealed record TicketTransferIndexViewModel(
    string ActiveTab,
    int PendingCount,
    IReadOnlyList<TicketTransferRowDto> Rows,
    IReadOnlyList<OrderDriftRow> Drift);

/// <summary>
/// Backs the one-page transfer wizard at /Tickets/Transfers. Step A picks a
/// ticket (<see cref="MyTickets"/>), step B picks a recipient via
/// &lt;vc:human-search&gt;, and once a (ticket, recipient) pair is confirmed
/// server-side, <see cref="Confirm"/> is populated to render step C.
/// </summary>
public sealed class TicketTransferWizardViewModel
{
    public IReadOnlyList<MyAttendeeRowDto> MyTickets { get; init; } = [];

    /// <summary>The holder's earliest entry date (across sources), stamped onto each
    /// selectable stub via <see cref="TicketStubInfo.From"/> so the EE pill matches
    /// the homepage/profile. Null when the holder has no Early Entry.</summary>
    public LocalDate? HolderEarlyEntry { get; init; }

    /// <summary>The user's own transfer requests (any status) — shown as a status/cancel list.</summary>
    public IReadOnlyList<TicketTransferRowDto> MyTransfers { get; init; } = [];

    /// <summary>Non-null on the confirm step: the resolved ticket + recipient summary.</summary>
    public TicketTransferConfirmDto? Confirm { get; init; }

    /// <summary>Reason re-rendered after a submit validation failure.</summary>
    public string? Reason { get; set; }

    /// <summary>Inline error shown at the top of the wizard.</summary>
    public string? Error { get; set; }
}
