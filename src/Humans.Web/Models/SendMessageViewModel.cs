using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public class SendMessageViewModel
{
    private string _message = string.Empty;

    public Guid RecipientId { get; set; }
    public string RecipientDisplayName { get; set; } = string.Empty;

    // Browsers submit textarea line breaks as \r\n regardless of how the user (or a
    // client-side character counter) perceives them, which would otherwise inflate the
    // bound string past what the user counted. Normalise on bind so the length check
    // below — and the stored/relayed message — agree with what the user sees.
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Message
    {
        get => _message;
        set => _message = value?.Replace("\r\n", "\n", StringComparison.Ordinal) ?? string.Empty;
    }

    public bool IncludeContactInfo { get; set; } = true;

    public string SenderEmail { get; set; } = string.Empty;
}
