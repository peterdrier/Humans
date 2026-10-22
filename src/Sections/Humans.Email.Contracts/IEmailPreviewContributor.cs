using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>
/// A sending section's samples for the template gallery at <c>/Email/EmailPreview</c>, built the way its real
/// send builds them.
/// </summary>
public interface IEmailPreviewContributor : IFanout
{
    /// <summary>Called once per culture. Must be pure: no persistence, no real recipient.</summary>
    IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona);
}

/// <summary>The fictional recipient a gallery cell is rendered for.</summary>
public sealed record EmailPreviewPersona(string Culture, string Name, string Email);

/// <summary>
/// <paramref name="Id"/> is a stable slug (the accordion key). <paramref name="Message"/> is unwrapped: the
/// gallery adds the branded body.
/// </summary>
public sealed record EmailPreviewSample(string Id, string Name, EmailMessage Message);
