using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Reflection;
using Humans.Base.Extensions;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Users.Tests.Services;

/// <summary>
/// Includes the gallery-coverage gate: every template <see cref="UsersEmails"/> can build has a
/// sample in <see cref="UsersEmailPreviews"/>, so a new template cannot ship invisible.
/// </summary>
public sealed class UsersEmailsTests
{
    private static readonly Instant DeletionDate = Instant.FromUtc(2026, 3, 15, 0, 0);

    private static UsersEmails Create() => TestUsersEmails.Create();

    [HumansFact]
    public void EmailVerification_IsAlwaysSend_NullCategory_AndTimeSensitive()
    {
        var msg = Create().EmailVerification(Guid.NewGuid(), "a@x.com", "Alice", "https://verify", isConflict: false, "en");

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

        emails.EmailVerification(Guid.NewGuid(), "a@x.com", "Alice", "https://verify", isConflict: false, "en")
            .HtmlBody.Should().Contain("https://verify");
        emails.EmailVerification(Guid.NewGuid(), "a@x.com", "Alice", "https://verify", isConflict: true, "en")
            .HtmlBody.Should().Contain("Users_Email_EmailVerification_Merge_Body");
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void AccountDeletionRequested_UsesRecipientCultureAndTheCancellationPage(string culture)
    {
        using var callerCulture = new CultureScope("en");
        string expectedDate;
        using (new CultureScope(culture))
            expectedDate = DeletionDate.InUtc().Date.ToDate();

        var localizer = new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var emails = new UsersEmails(
            Options.Create(new EmailSettings { BaseUrl = TestUsersEmails.BaseUrl }),
            localizer, NullLogger<UsersEmails>.Instance);
        var msg = emails.AccountDeletionRequested(Guid.NewGuid(), "a@x.com", "Alice", DeletionDate, culture);

        msg.TemplateName.Should().Be("deletion_requested");
        msg.Category.Should().BeNull();
        msg.Culture.Should().Be(culture);
        msg.HtmlBody.Should().Contain(expectedDate);
        msg.HtmlBody.Should().Contain($"href=\"{TestUsersEmails.BaseUrl}/Gdpr/Deletion\"");
    }

    [HumansFact]
    public void AccountDeleted_IsNeverPersisted()
    {
        var msg = Create().AccountDeleted(Guid.NewGuid(), "a@x.com", "Alice", "en");

        msg.TemplateName.Should().Be("account_deleted");
        // Article 17: an outbox row would re-create the address the cascade just erased.
        msg.DoNotPersist.Should().BeTrue();
        msg.Category.Should().BeNull();
    }

    [HumansFact]
    public void AccessSuspended_IsAlwaysSend_AndEncodesTheReason()
    {
        var msg = Create().AccessSuspended(Guid.NewGuid(), "a@x.com", "Alice", "Missing <consent>", "en");

        msg.TemplateName.Should().Be("access_suspended");
        msg.Category.Should().BeNull();
        msg.HtmlBody.Should().Contain("Missing &lt;consent&gt;");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture()
    {
        Create().AccountDeleted(Guid.NewGuid(), "a@x.com", "Alice", "es")
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
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(bool)) return false;
        if (type == typeof(Instant)) return DeletionDate;
        throw new NotSupportedException(
            $"UsersEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
