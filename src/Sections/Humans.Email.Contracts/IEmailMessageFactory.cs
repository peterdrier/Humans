namespace Humans.Email.Contracts;

/// <summary>
/// The one template Email owns: the member-to-member relay, sent by Email, Camps and Teams. Add no other templates
/// here; each belongs to the section that sends it.
/// </summary>
public interface IEmailMessageFactory
{
    /// <summary>Reply-to is <paramref name="replyTo"/> when given (a team's group address), else the sender when
    /// contact info is shared. <paramref name="recipientUserId"/> is null only for an address no human owns
    /// (a camp's contact address).</summary>
    EmailMessage FacilitatedMessage(Guid? recipientUserId, string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null, string? replyTo = null);
}
