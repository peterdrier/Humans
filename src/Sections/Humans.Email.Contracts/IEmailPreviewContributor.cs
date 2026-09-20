using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>
/// A sending section's contribution to the rendered-template gallery at
/// <c>/Email/EmailPreview</c>: one sample per template it sends, built the same way
/// the real send path builds it. A section implements this beside its email builder
/// and registers it from <c>Section.Register</c>; the gallery injects
/// <c>IEnumerable&lt;IEmailPreviewContributor&gt;</c> and asks all of them.
/// </summary>
/// <remarks>
/// The gallery is how templates stay visible once each section owns its own
/// (peterdrier/Humans#1651). Contributing rather than being listed centrally is what
/// keeps Email from naming every sending section.
/// </remarks>
public interface IEmailPreviewContributor : IFanout
{
    /// <summary>
    /// Samples for one gallery cell. Called once per culture and must be pure —
    /// no persistence, no real recipient, sample data only.
    /// </summary>
    IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona);
}

/// <summary>The fictional recipient a gallery cell is rendered for.</summary>
public sealed record EmailPreviewPersona(string Culture, string Name, string Email);

/// <summary>
/// One gallery entry. <paramref name="Id"/> is a stable slug used as the accordion
/// key; <paramref name="Name"/> is the human label; <paramref name="Message"/> is the
/// message the section would send, unwrapped — the gallery composes the branded body.
/// </summary>
public sealed record EmailPreviewSample(string Id, string Name, EmailMessage Message);
