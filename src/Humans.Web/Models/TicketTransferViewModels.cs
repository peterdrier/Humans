namespace Humans.Web.Models;

public sealed class TicketTransferRequestPageViewModel
{
    public Guid AttendeeId { get; set; }
    public string AttendeeName { get; set; } = string.Empty;
    public string TicketTypeName { get; set; } = string.Empty;
    public string? Query { get; set; }
    public RecipientCardViewModel? Recipient { get; set; }
    public string? LookupError { get; set; }
}

public sealed class RecipientCardViewModel
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
    public Guid RecipientUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
