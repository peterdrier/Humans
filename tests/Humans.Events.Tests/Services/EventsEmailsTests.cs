using System.Reflection;
using System.Globalization;
using Humans.Base.Extensions;
using Xunit;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Events.Tests.Services;

/// <summary>
/// The per-template routing policy Events stamps on its own messages — one template name
/// per lifecycle status — plus the gallery-coverage gate: every template
/// <see cref="EventsEmails"/> can build has a sample in
/// <see cref="EventsEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class EventsEmailsTests
{
    private static EventsEmails Create() => new(NullLogger<EventsEmails>.Instance,
        new StringLocalizer<EventsResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)));

    [HumansTheory]
    [InlineData("en", EventStatus.Pending, "Your event submission has been received", "Hi")]
    [InlineData("en", EventStatus.Approved, "Your event has been approved", "Hi")]
    [InlineData("en", EventStatus.Rejected, "Your event submission was not approved", "Hi")]
    [InlineData("en", EventStatus.ResubmitRequested, "Changes requested for your event submission", "Hi")]
    [InlineData("es", EventStatus.Pending, "Hemos recibido tu propuesta de evento", "Hola")]
    [InlineData("es", EventStatus.Approved, "Tu evento ha sido aprobado", "Hola")]
    [InlineData("es", EventStatus.Rejected, "Tu propuesta de evento no ha sido aprobada", "Hola")]
    [InlineData("es", EventStatus.ResubmitRequested, "Se solicitan cambios en tu propuesta de evento", "Hola")]
    [InlineData("de", EventStatus.Pending, "Deine Veranstaltung wurde eingereicht", "Hallo")]
    [InlineData("de", EventStatus.Approved, "Deine Veranstaltung wurde genehmigt", "Hallo")]
    [InlineData("de", EventStatus.Rejected, "Deine Veranstaltung wurde nicht genehmigt", "Hallo")]
    [InlineData("de", EventStatus.ResubmitRequested, "Änderungen an deiner Veranstaltung erforderlich", "Hallo")]
    [InlineData("it", EventStatus.Pending, "Abbiamo ricevuto la tua proposta di evento", "Ciao")]
    [InlineData("it", EventStatus.Approved, "Il tuo evento è stato approvato", "Ciao")]
    [InlineData("it", EventStatus.Rejected, "La tua proposta di evento non è stata approvata", "Ciao")]
    [InlineData("it", EventStatus.ResubmitRequested, "Sono richieste modifiche alla tua proposta di evento", "Ciao")]
    [InlineData("fr", EventStatus.Pending, "Nous avons reçu ta proposition d’événement", "Bonjour")]
    [InlineData("fr", EventStatus.Approved, "Ton événement a été approuvé", "Bonjour")]
    [InlineData("fr", EventStatus.Rejected, "Ta proposition d’événement n’a pas été approuvée", "Bonjour")]
    [InlineData("fr", EventStatus.ResubmitRequested, "Des modifications sont demandées pour ton événement", "Bonjour")]
    [InlineData("ca", EventStatus.Pending, "Hem rebut la teva proposta d’esdeveniment", "Hola")]
    [InlineData("ca", EventStatus.Approved, "El teu esdeveniment ha estat aprovat", "Hola")]
    [InlineData("ca", EventStatus.Rejected, "La teva proposta d’esdeveniment no ha estat aprovada", "Hola")]
    [InlineData("ca", EventStatus.ResubmitRequested, "Es demanen canvis en la teva proposta d’esdeveniment", "Hola")]
    public void EventLifecycle_UsesRecipientCultureAndEncodesSubstitutions(
        string culture, EventStatus status, string subject, string greeting)
    {
        using var ambient = new CultureScope("en");
        var msg = Create().EventLifecycle(
            new EventLifecycleNotification(status, "Bob <Admin>", "Lights & sound", "Too <loud>",
                "/edit?a=1&b=2", culture), "bob@x.com");

        msg.Subject.Should().Be(subject);
        msg.HtmlBody.Should().Contain($"<p>{greeting} Bob &lt;Admin&gt;,</p>");
        msg.HtmlBody.Should().Contain("Lights &amp; sound");
        if (status is EventStatus.Rejected or EventStatus.ResubmitRequested)
            msg.HtmlBody.Should().Contain("Too &lt;loud&gt;");
        if (status is not EventStatus.Approved)
            msg.HtmlBody.Should().Contain("/edit?a=1&amp;b=2");
        CultureInfo.CurrentUICulture.Name.Should().Be("en");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansFact]
    public void EventLifecycle_PicksTemplateFromStatus()
    {
        var emails = Create();

        emails.EventLifecycle(new EventLifecycleNotification(EventStatus.Pending, "Bob", "My Event"), "bob@x.com")
            .TemplateName.Should().Be("event_submitted");
        emails.EventLifecycle(new EventLifecycleNotification(EventStatus.Approved, "Bob", "My Event"), "bob@x.com")
            .TemplateName.Should().Be("event_approved");
        emails.EventLifecycle(new EventLifecycleNotification(EventStatus.Rejected, "Bob", "My Event"), "bob@x.com")
            .TemplateName.Should().Be("event_rejected");
        emails.EventLifecycle(new EventLifecycleNotification(EventStatus.ResubmitRequested, "Bob", "My Event"), "bob@x.com")
            .TemplateName.Should().Be("event_resubmit_requested");
    }

    [HumansFact]
    public void EventLifecycle_RoutesToTheSubmitterWithNoCategory()
    {
        var msg = Create().EventLifecycle(
            new EventLifecycleNotification(EventStatus.Approved, "Bob", "My Event"), "bob@x.com");

        msg.RecipientEmail.Should().Be("bob@x.com");
        msg.RecipientName.Should().Be("Bob");
        msg.Category.Should().BeNull();
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void EventLifecycle_EncodesTheSubmitterNameAndTitle()
    {
        var msg = Create().EventLifecycle(
            new EventLifecycleNotification(EventStatus.Rejected, "Daniel <Admin>", "Lights & sound", "Too <loud>", "/edit"),
            "bob@x.com");

        msg.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        msg.HtmlBody.Should().Contain("Lights &amp; sound");
        msg.HtmlBody.Should().Contain("Too &lt;loud&gt;");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new EventsEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Events template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new EventsEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// The lifecycle builder is one method over four statuses, so each status is sampled.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(EventsEmails emails) =>
    [
        .. typeof(EventsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .SelectMany(m => LifecycleStatuses.Select(status =>
                ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(p => SampleArgument(p, status))])!).TemplateName))
    ];

    private static readonly EventStatus[] LifecycleStatuses =
    [
        EventStatus.Pending, EventStatus.Approved, EventStatus.Rejected, EventStatus.ResubmitRequested,
    ];

    private static object SampleArgument(ParameterInfo parameter, EventStatus status)
    {
        var type = parameter.ParameterType;
        if (type == typeof(EventLifecycleNotification))
            return new EventLifecycleNotification(status, "Sally Smith", "Sample", "Reason", "/Events/Mine", "en");
        if (type == typeof(string)) return "sample@example.com";
        throw new NotSupportedException(
            $"EventsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
