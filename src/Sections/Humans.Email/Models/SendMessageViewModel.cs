using System.ComponentModel.DataAnnotations;

namespace Humans.Email.Models;

internal sealed class SendMessageViewModel
{
    private string _message = string.Empty;

    public Guid RecipientId { get; set; }
    public string RecipientDisplayName { get; set; } = string.Empty;

    // Browsers submit line breaks as \r\n, which would push the bound string past what the member counted.
    // Normalising on bind keeps the length check and the relayed message in line with what they see.
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
