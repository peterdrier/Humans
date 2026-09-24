using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Expenses.Services;
using Humans.Expenses.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// The per-template routing policy Expenses stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="ExpensesEmails"/> can build has a sample in
/// <see cref="ExpensesEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class ExpensesEmailsTests
{
    private static ExpensesEmails Create() => TestExpensesEmails.Create();

    [HumansFact]
    public void ReportApproved_StampsSystem_AndLinksTheReport()
    {
        var reportId = Guid.NewGuid();
        var msg = Create().ReportApproved("a@x.com", "Alice <A>", reportId, 1234.5m, "ES79****789", "de");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice <A>");
        msg.TemplateName.Should().Be("expense_approved");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
        msg.Subject.Should().EndWith("#de");
        msg.HtmlBody.Should().Contain("Alice &lt;A&gt;");
        msg.HtmlBody.Should().Contain("1.234,50 €");
        msg.HtmlBody.Should().Contain("ES79****789");
        msg.HtmlBody.Should().Contain($"{TestExpensesEmails.BaseUrl}/Expenses/{reportId}");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new ExpensesEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Expenses template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new ExpensesEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(ExpensesEmails emails) =>
    [
        .. typeof(ExpensesEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(decimal)) return 1m;
        if (type == typeof(Guid)) return Guid.NewGuid();
        throw new NotSupportedException(
            $"ExpensesEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
