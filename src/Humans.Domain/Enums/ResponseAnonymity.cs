namespace Humans.Domain.Enums;

/// <summary>
/// Anonymity tier of a response. <see cref="Identified"/> is the only tier that is personal data
/// (links UserId + InvitationId, resumable, GDPR-exported). <see cref="CompletionTracked"/> counts
/// participation but stores no link. <see cref="Anonymous"/> leaves no trace.
/// </summary>
public enum ResponseAnonymity
{
    Identified = 0,
    CompletionTracked = 1,
    Anonymous = 2
}
