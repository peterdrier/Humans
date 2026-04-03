namespace Humans.Application.Interfaces;

/// <summary>
/// Orchestrates the facilitated-contact workflow for camps:
/// rate limiting, message sanitization, email send, and audit logging.
/// </summary>
public interface ICampContactService
{
    /// <summary>
    /// Send a facilitated contact message from a user to a camp.
    /// Handles rate limiting, HTML sanitization, email delivery, and audit logging.
    /// Returns a result indicating success or the reason for failure.
    /// </summary>
    Task<CampContactResult> SendFacilitatedMessageAsync(
        Guid campId,
        string campContactEmail,
        string campDisplayName,
        Guid senderUserId,
        string senderDisplayName,
        string senderEmail,
        string message,
        bool includeContactInfo);
}

/// <summary>Result of a facilitated contact attempt.</summary>
public record CampContactResult(bool Success, bool RateLimited);
