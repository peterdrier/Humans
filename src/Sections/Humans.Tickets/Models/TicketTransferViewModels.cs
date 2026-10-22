using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using NodaTime;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Models;

internal sealed record TicketTransferIndexViewModel(
    string ActiveTab,
    int PendingCount,
    IReadOnlyList<TicketTransferRowDto> Rows,
    IReadOnlyList<OrderDriftRow> Drift);

internal sealed class TicketTransferWizardViewModel
{
    public IReadOnlyList<MyAttendeeRowDto> MyTickets { get; init; } = [];

    /// <summary>The holder's own earliest entry, stamped on each stub via <see cref="TicketStubInfo.From"/>.</summary>
    public LocalDate? HolderEarlyEntry { get; init; }

    public IReadOnlyList<TicketTransferRowDto> MyTransfers { get; init; } = [];

    /// <summary>Non-null on the confirm step.</summary>
    public TicketTransferConfirmDto? Confirm { get; init; }

    public string? Reason { get; set; }

    public string? Error { get; set; }
}
