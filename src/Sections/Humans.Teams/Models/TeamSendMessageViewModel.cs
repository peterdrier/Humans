using System.ComponentModel.DataAnnotations;

namespace Humans.Teams.Models;

internal sealed class TeamSendMessageViewModel
{
    private string _message = string.Empty;

    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamReplyToEmail { get; set; } = string.Empty;

    public Guid RecipientId { get; set; }
    public string RecipientDisplayName { get; set; } = string.Empty;

    // Browsers submit textarea line breaks as \r\n: normalise on bind so the length check agrees with what the sender
    // counted.
    [Required(ErrorMessage = "Validation_Required")]
    [StringLength(2000, ErrorMessage = "Validation_MaxLength")]
    public string Message
    {
        get => _message;
        set => _message = value?.Replace("\r\n", "\n", StringComparison.Ordinal) ?? string.Empty;
    }

    public bool IncludeContactInfo { get; set; } = true;

    public string SenderEmail { get; set; } = string.Empty;
}
