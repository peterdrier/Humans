using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>
/// Users' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="UsersEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members
/// receive (peterdrier/Humans#1651). Sample data only — no recipient is real and
/// nothing is sent.
/// </summary>
internal sealed class UsersEmailPreviews(UsersEmails emails, IOptions<EmailSettings> settings) : IEmailPreviewContributor
{
    private const string SampleSuspensionReason = "Outstanding consent requirements";
    private static readonly Instant SampleDeletionDate = Instant.FromUtc(2026, 3, 15, 0, 0);

    private readonly EmailSettings _settings = settings.Value;

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);
        var verificationUrl = $"{_settings.BaseUrl}/Profile/VerifyEmail?token=sample-token";

        return
        [
            new EmailPreviewSample("access-suspended", "Access Suspended",
                emails.AccessSuspended(email, name, SampleSuspensionReason, culture)),

            // Both arms of the verification mail: the plain one and the merge variant a
            // second account with the same address gets.
            new EmailPreviewSample("email-verification", "Email Verification",
                emails.EmailVerification("newemail@example.com", name, verificationUrl, isConflict: false, culture)),
            new EmailPreviewSample("email-verification-merge", "Email Verification (Merge)",
                emails.EmailVerification("duplicate@example.com", name, verificationUrl, isConflict: true, culture)),

            new EmailPreviewSample("deletion-requested", "Account Deletion Requested",
                emails.AccountDeletionRequested(email, name, SampleDeletionDate, culture)),
            new EmailPreviewSample("account-deleted", "Account Deleted",
                emails.AccountDeleted(email, name, culture)),
        ];
    }
}
