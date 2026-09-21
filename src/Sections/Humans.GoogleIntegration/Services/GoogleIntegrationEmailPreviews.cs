using Humans.Email.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// GoogleIntegration's contribution to the template gallery at
/// <c>/Email/EmailPreview</c>: one sample per template
/// <see cref="GoogleIntegrationEmails"/> can send, built through the same builder the
/// real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class GoogleIntegrationEmailPreviews(GoogleIntegrationEmails emails) : IEmailPreviewContributor
{
    private const string SampleGroupName = "Art Collective";
    private const string SampleGroupEmail = "art-collective@nobodies.team";
    private const string SampleFolderName = "Art Collective Shared Drive";
    private const string SampleWorkspaceEmail = "sally@nobodies.team";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("workspace-credentials", "Workspace Credentials",
                emails.WorkspaceCredentials(email, name, SampleWorkspaceEmail, "sample-temp-password", culture)),
            new EmailPreviewSample("google-group-removal-loss", "Google Group Removal — Loss of Access",
                emails.GoogleGroupRemovalLossOfAccess(email, name, SampleGroupName, SampleGroupEmail, culture)),
            new EmailPreviewSample("google-drive-removal-loss", "Google Drive Removal — Loss of Access",
                emails.GoogleDriveRemovalLossOfAccess(email, name, SampleFolderName, culture)),
            new EmailPreviewSample("google-removal-secondary-cleanup", "Google Access Removal — Secondary Email Cleanup",
                emails.GoogleAccessRemovalSecondaryCleanup("old-" + email, name, email, culture)),
        ];
    }
}
