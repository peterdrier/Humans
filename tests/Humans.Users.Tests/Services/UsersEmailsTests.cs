using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Users.Tests.Services;

/// <summary>
/// The per-template routing policy Users stamps on its own messages — template name,
/// always-send (no opt-out category), the time-sensitive verification name the outbox
/// drains immediately, and the do-not-persist rule on the erasure confirmation — plus
/// the gallery-coverage gate: every template <see cref="UsersEmails"/> can build has a
/// sample in <see cref="UsersEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class UsersEmailsTests
{
    private static readonly Instant DeletionDate = Instant.FromUtc(2026, 3, 15, 0, 0);

    private static UsersEmails Create() => TestUsersEmails.Create();

    [HumansFact]
    public void EmailVerification_IsAlwaysSend_NullCategory_AndTimeSensitive()
    {
        var msg = Create().EmailVerification("a@x.com", "Alice", "https://verify", isConflict: false, "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.TemplateName.Should().Be(TimeSensitiveTemplates.EmailVerification);
        TimeSensitiveTemplates.Names.Should().Contain(msg.TemplateName);
        msg.Category.Should().BeNull();
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void EmailVerification_ConflictPicksTheMergeBody()
    {
        var emails = Create();

        emails.EmailVerification("a@x.com", "Alice", "https://verify", isConflict: false, "en")
            .HtmlBody.Should().Contain("https://verify");
        emails.EmailVerification("a@x.com", "Alice", "https://verify", isConflict: true, "en")
            .HtmlBody.Should().Contain("Users_Email_EmailVerification_Merge_Body");
    }

    [HumansFact]
    public void AccountDeletionRequested_FormatsTheDateAsAnInvariantLongDate()
    {
        var msg = Create().AccountDeletionRequested("a@x.com", "Alice", DeletionDate, "en");

        msg.TemplateName.Should().Be("deletion_requested");
        msg.Category.Should().BeNull();
        msg.HtmlBody.Should().Contain("15 March 2026");
    }

    [HumansFact]
    public void AccountDeleted_IsNeverPersisted()
    {
        var msg = Create().AccountDeleted("a@x.com", "Alice", "en");

        msg.TemplateName.Should().Be("account_deleted");
        // Article 17: an outbox row would re-create the address the cascade just erased.
        msg.DoNotPersist.Should().BeTrue();
        msg.Category.Should().BeNull();
    }

    [HumansFact]
    public void AccessSuspended_IsAlwaysSend_AndEncodesTheReason()
    {
        var msg = Create().AccessSuspended("a@x.com", "Alice", "Missing <consent>", "en");

        msg.TemplateName.Should().Be("access_suspended");
        msg.Category.Should().BeNull();
        msg.HtmlBody.Should().Contain("Missing &lt;consent&gt;");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture()
    {
        Create().AccountDeleted("a@x.com", "Alice", "es")
            .Subject.Should().Be("Users_Email_AccountDeleted_Subject#es");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = Previews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName)
            .Distinct(StringComparer.Ordinal);

        sampled.Should().BeEquivalentTo(templates,
            "every Users template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = Previews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    private static UsersEmailPreviews Previews(UsersEmails emails) =>
        new(emails, Options.Create(new EmailSettings { BaseUrl = TestUsersEmails.BaseUrl }));

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(UsersEmails emails) =>
    [
        .. typeof(UsersEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(bool)) return false;
        if (type == typeof(Instant)) return DeletionDate;
        throw new NotSupportedException(
            $"UsersEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
