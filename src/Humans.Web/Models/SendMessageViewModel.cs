using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public class SendMessageViewModel
{
    public Guid RecipientId { get; set; }
    public string RecipientDisplayName { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    public bool IncludeContactInfo { get; set; } = true;

    public string SenderEmail { get; set; } = string.Empty;
}
