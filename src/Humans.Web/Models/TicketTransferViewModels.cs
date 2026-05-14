using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;

namespace Humans.Web.Models;

public sealed record TicketTransferIndexViewModel(
    string ActiveTab,
    int PendingCount,
    int NeedsAttentionCount,
    IReadOnlyList<TicketTransferRowDto> Rows,
    IReadOnlyList<OrderDriftRow> Drift);

public sealed class TicketTransferRequestPageViewModel
{
    public Guid AttendeeId { get; set; }
    public string AttendeeName { get; set; } = string.Empty;
    public string TicketTypeName { get; set; } = string.Empty;
    public string? Query { get; set; }

    /// <summary>
    /// Lookup candidates. Email queries return 0 or 1; burner-name queries
    /// return 0..10. View renders accordingly: 0 → not-found message, 1 →
    /// confirm + reason form, &gt;1 → picker with "Choose" buttons.
    /// </summary>
    public List<ReceiverCardViewModel> Receivers { get; set; } = new();

    public string? LookupError { get; set; }

    /// <summary>
    /// Reason text re-rendered into the confirm form's textarea after a Submit
    /// validation failure so the Sender doesn't lose their typing.
    /// </summary>
    public string? PrefilledReason { get; set; }
}

public sealed class ReceiverCardViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? BurnerName { get; set; }
    public string? PreferredEmail { get; set; }
    public bool HasCustomProfilePicture { get; set; }
    public string? ProfilePictureUrl { get; set; }
}

public sealed class TicketTransferConfirmFormViewModel
{
    public Guid AttendeeId { get; set; }
    public Guid ReceiverUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
