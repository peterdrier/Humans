using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The copy and the per-kind routing policy Workgroups stamps on its own notices, plus the
/// gallery-coverage gate: every template <see cref="WorkgroupsEmails"/> can build has a
/// sample in <see cref="WorkgroupsEmailPreviews"/>, so a new notice kind cannot ship
/// invisible (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue)
/// stays Email's, covered there.
/// </summary>
public sealed class WorkgroupsEmailsTests
{
    private static readonly EmailPreviewPersona Persona = new("en", "Sally Smith", "sally@example.com");

    [HumansFact]
    public void WorkgroupNotice_EveryKindEveryCulture_RendersNonEmptyContentWithNoRawKeyLeak()
    {
        var emails = TestWorkgroupsEmails.CreateReal();

        foreach (var culture in CultureCatalog.SupportedCultureCodes)
        {
            foreach (var kind in Enum.GetValues<WorkgroupNoticeKind>())
            {
                var msg = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
                    RecipientEmail: "coord@x.com",
                    RecipientName: "Coord",
                    Kind: kind,
                    WorkgroupName: "Safety",
                    WorkgroupSlug: "safety",
                    Detail: "Some detail",
                    Culture: culture));

                msg.Subject.Should().NotBeNullOrWhiteSpace(because: $"{kind}/{culture} subject");
                msg.HtmlBody.Should().NotBeNullOrWhiteSpace(because: $"{kind}/{culture} body");
                msg.Subject.Should().NotContain("Workgroups_Email_", because: $"{kind}/{culture} subject leaked a raw key");
                msg.HtmlBody.Should().NotContain("Workgroups_Email_", because: $"{kind}/{culture} body leaked a raw key");
                msg.HtmlBody.Should().Contain("safety", because: "the working-group link is built from the slug");
            }
        }
    }

    [HumansFact]
    public void WorkgroupNotice_DetailLine_AppearsWhenSuppliedAndBodyIsWellFormedWhenNull()
    {
        var emails = TestWorkgroupsEmails.CreateReal();

        var withReason = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Refused, "Safety", "safety", Detail: "not enough scope", Culture: "en"));
        withReason.HtmlBody.Should().Contain("not enough scope");

        var withoutReason = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Refused, "Safety", "safety", Detail: null, Culture: "en"));
        withoutReason.HtmlBody.Should().NotBeNullOrWhiteSpace();
        withoutReason.HtmlBody.Should().NotContain("{2}");
    }

    [HumansFact]
    public void WorkgroupNotice_NullRecipientName_UsesGenericGreeting()
    {
        var msg = TestWorkgroupsEmails.CreateReal().WorkgroupNotice(new WorkgroupNoticeRequest(
            "board@x.com", null, WorkgroupNoticeKind.Applied, "Safety", "safety", Culture: "en"));

        msg.HtmlBody.Should().Contain("Hello,");
        msg.HtmlBody.Should().NotContain("Hi ,");
    }

    [HumansFact]
    public void WorkgroupNotice_CultureSelectsLanguage()
    {
        var emails = TestWorkgroupsEmails.CreateReal();

        var en = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Registered, "Safety", "safety", Culture: "en"));
        var es = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Registered, "Safety", "safety", Culture: "es"));

        en.Subject.Should().Be("Working group registered: Safety");
        es.Subject.Should().Be("Grupo de trabajo registrado: Safety");
    }

    [HumansFact]
    public void WorkgroupNotice_NameWithAnAmpersand_StaysRawInTheSubjectAndEncodedInTheBody()
    {
        var msg = TestWorkgroupsEmails.CreateReal().WorkgroupNotice(new WorkgroupNoticeRequest(
            "board@x.com", "Ada", WorkgroupNoticeKind.Applied, "Health & Safety", "health-safety",
            Culture: "en"));

        // The subject is plain text; the body is HTML. One encoded value cannot serve both.
        msg.Subject.Should().Contain("Health & Safety").And.NotContain("&amp;");
        msg.HtmlBody.Should().Contain("Health &amp; Safety");
    }

    [HumansFact]
    public void WorkgroupNotice_StampsGovernanceAndRecipient()
    {
        var msg = TestWorkgroupsEmails.Create().WorkgroupNotice(new WorkgroupNoticeRequest(
            RecipientEmail: "coord@x.com",
            RecipientName: "Coord",
            Kind: WorkgroupNoticeKind.Registered,
            WorkgroupName: "Safety",
            WorkgroupSlug: "safety",
            Culture: "en"));

        msg.RecipientEmail.Should().Be("coord@x.com");
        msg.RecipientName.Should().Be("Coord");
        msg.TemplateName.Should().Be("workgroup_notice_registered");
        msg.Category.Should().Be(MessageCategory.Governance);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void WorkgroupNotice_TemplateNamePerKind()
    {
        var expected = new Dictionary<WorkgroupNoticeKind, string>
        {
            [WorkgroupNoticeKind.Applied] = "workgroup_notice_applied",
            [WorkgroupNoticeKind.Referred] = "workgroup_notice_referred",
            [WorkgroupNoticeKind.Registered] = "workgroup_notice_registered",
            [WorkgroupNoticeKind.Refused] = "workgroup_notice_refused",
            [WorkgroupNoticeKind.Withdrawn] = "workgroup_notice_withdrawn",
            [WorkgroupNoticeKind.Ended] = "workgroup_notice_ended",
            [WorkgroupNoticeKind.Reactivated] = "workgroup_notice_reactivated",
            [WorkgroupNoticeKind.CoordinatorsChanged] = "workgroup_notice_coordinators_changed",
            [WorkgroupNoticeKind.DormancyInquiry] = "workgroup_notice_dormancy_inquiry",
            [WorkgroupNoticeKind.Delivered] = "workgroup_notice_delivered",
            [WorkgroupNoticeKind.DispositionRecorded] = "workgroup_notice_disposition_recorded",
        };
        var emails = TestWorkgroupsEmails.Create();

        foreach (var (kind, templateName) in expected)
        {
            var msg = emails.WorkgroupNotice(new WorkgroupNoticeRequest(
                "a@x.com", "A", kind, "Safety", "safety", Culture: "en"));

            msg.TemplateName.Should().Be(templateName, because: $"{kind} keys its own metric");
        }
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = TestWorkgroupsEmails.Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new WorkgroupsEmailPreviews(emails).Samples(Persona)
            .Select(s => s.Message.TemplateName);

        // Registered samples twice (named greeting, role-inbox generic greeting), so
        // compare distinct sets.
        sampled.Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(templates,
            "every Workgroups template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new WorkgroupsEmailPreviews(TestWorkgroupsEmails.Create()).Samples(Persona);

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// notice kind added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(WorkgroupsEmails emails) =>
    [
        .. typeof(WorkgroupsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .SelectMany(m => Enum.GetValues<WorkgroupNoticeKind>().Select(kind =>
                ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(p => SampleArgument(p, kind))])!).TemplateName))
            .Distinct(StringComparer.Ordinal)
    ];

    private static object SampleArgument(ParameterInfo parameter, WorkgroupNoticeKind kind)
    {
        if (parameter.ParameterType == typeof(WorkgroupNoticeRequest))
        {
            return new WorkgroupNoticeRequest("a@x.com", "A", kind, "Safety", "safety", "detail", "en");
        }

        throw new NotSupportedException(
            $"WorkgroupsEmails.{parameter.Member.Name} takes a {parameter.ParameterType.Name}; teach this test how to sample one.");
    }
}
