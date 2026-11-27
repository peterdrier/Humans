using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Tasks.Services;
using Humans.Testing;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

/// <summary>Both emails over the real resx copy, in every supported culture.</summary>
public sealed class TasksEmailsTests
{
    private readonly TaskTestKit _kit = new();

    private static TaskAnswerEmail Request(string culture, string? reason = null) => new(
        Guid.NewGuid(), "sam@example.com", "Sam <Spark>", Guid.NewGuid(), "Fix the <gate> & sign", "Gate & Co", reason, culture);

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Approved_RendersLocalizedEncodedBody(string culture)
    {
        var request = Request(culture);

        var mail = _kit.Emails.RequestApproved(request);

        mail.Culture.Should().Be(culture);
        mail.Category.Should().Be(MessageCategory.VolunteerUpdates);
        mail.TemplateName.Should().Be("task_request_approved");
        mail.UserId.Should().Be(request.RecipientUserId);
        mail.Subject.Should().Contain("Fix the <gate> & sign", "subjects are plain text").And.NotContain("Tasks_Email_");
        mail.HtmlBody.Should().Contain("Fix the &lt;gate&gt; &amp; sign").And.Contain("Gate &amp; Co").And.Contain("Sam &lt;Spark&gt;")
            .And.Contain($"https://humans.test/Tasks/{request.TaskId}").And.NotContain("Tasks_Email_").And.NotContain("<gate>");
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Declined_CarriesTheEncodedReasonOnlyWhenThereIsOne(string culture)
    {
        var with = _kit.Emails.RequestDeclined(Request(culture, "Too <many> cooks"));
        var without = _kit.Emails.RequestDeclined(Request(culture));

        with.TemplateName.Should().Be("task_request_declined");
        with.Category.Should().Be(MessageCategory.VolunteerUpdates);
        with.HtmlBody.Should().Contain("Too &lt;many&gt; cooks").And.NotContain("<many>").And.NotContain("Tasks_Email_");
        without.HtmlBody.Should().NotContain("cooks").And.NotContain("Tasks_Email_");
        without.HtmlBody.Length.Should().BeLessThan(with.HtmlBody.Length);
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.Translated), MemberType = typeof(SupportedCultures))]
    public void Translations_DifferFromEnglish(string culture)
    {
        var english = _kit.Emails.RequestApproved(Request("en"));
        var translated = _kit.Emails.RequestApproved(Request(culture));
        var englishNo = _kit.Emails.RequestDeclined(Request("en"));
        var translatedNo = _kit.Emails.RequestDeclined(Request(culture));

        translated.Subject.Should().NotBe(english.Subject);
        translated.HtmlBody.Should().NotBe(english.HtmlBody);
        translatedNo.Subject.Should().NotBe(englishNo.Subject);
        translatedNo.HtmlBody.Should().NotBe(englishNo.HtmlBody);
    }

    [HumansFact]
    public void Gallery_HasSampleForEveryTemplate()
    {
        var persona = new EmailPreviewPersona("en", "Sam", "sam@example.com");

        var samples = new TasksEmailPreviews(_kit.Emails).Samples(persona);

        var builders = typeof(TasksEmails).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(EmailMessage)).Select(m => m.Name).ToList();
        builders.Should().HaveCount(2);
        samples.Select(s => s.Message.TemplateName).Distinct(StringComparer.Ordinal)
            .Should().BeEquivalentTo("task_request_approved", "task_request_declined");
        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        samples.Should().OnlyContain(s => s.Message.Category == MessageCategory.VolunteerUpdates && s.Message.RecipientEmail == "sam@example.com");
    }
}
