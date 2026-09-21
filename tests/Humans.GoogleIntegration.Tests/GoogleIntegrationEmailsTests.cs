using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The per-template routing policy GoogleIntegration stamps on its own messages —
/// template name, the System category that suppresses the unsubscribe footer, and the
/// time-sensitive credentials name the outbox drains immediately — plus the
/// gallery-coverage gate: every template <see cref="GoogleIntegrationEmails"/> can build
/// has a sample in <see cref="GoogleIntegrationEmailPreviews"/>, so a new template cannot
/// ship invisible (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping,
/// enqueue) stays Email's, covered there.
/// </summary>
public sealed class GoogleIntegrationEmailsTests
{
    private static GoogleIntegrationEmails Create() => TestGoogleIntegrationEmails.Create();

    [HumansFact]
    public void WorkspaceCredentials_IsAlwaysSend_AndTimeSensitive()
    {
        var msg = Create().WorkspaceCredentials("recovery@x.com", "Alice", "alice@nobodies.team", "hunter2", "en");

        msg.RecipientEmail.Should().Be("recovery@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be(TimeSensitiveTemplates.WorkspaceCredentials);
        TimeSensitiveTemplates.Names.Should().Contain(msg.TemplateName);
        msg.Category.Should().BeNull();
        msg.HtmlBody.Should().Contain("alice@nobodies.team").And.Contain("hunter2");
    }

    [HumansFact]
    public void RemovalNotices_StampSystem_SoNoUnsubscribeFooterIsAdded()
    {
        var emails = Create();

        var group = emails.GoogleGroupRemovalLossOfAccess("a@x.com", "Alice", "Art", "art@nobodies.team", "en");
        group.TemplateName.Should().Be("google_group_removal_loss_of_access");
        group.Category.Should().Be(MessageCategory.System);

        var drive = emails.GoogleDriveRemovalLossOfAccess("a@x.com", "Alice", "Shared Drive", "en");
        drive.TemplateName.Should().Be("google_drive_removal_loss_of_access");
        drive.Category.Should().Be(MessageCategory.System);

        var cleanup = emails.GoogleAccessRemovalSecondaryCleanup("old@x.com", "Alice", "new@x.com", "en");
        cleanup.TemplateName.Should().Be("google_access_removal_secondary_cleanup");
        cleanup.Category.Should().Be(MessageCategory.System);
        cleanup.RecipientEmail.Should().Be("old@x.com");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture_AndEncodesTheResourceName()
    {
        var msg = Create().GoogleDriveRemovalLossOfAccess("a@x.com", "Alice", "Lights & sound", "es");

        msg.Subject.Should().EndWith("#es");
        msg.HtmlBody.Should().Contain("Lights &amp; sound");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new GoogleIntegrationEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every GoogleIntegration template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new GoogleIntegrationEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(GoogleIntegrationEmails emails) =>
    [
        .. typeof(GoogleIntegrationEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"GoogleIntegrationEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
