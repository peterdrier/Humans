using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Governance.Services;
using Humans.Governance.Tests.Infrastructure;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// The per-template routing policy Governance stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="GovernanceEmails"/> can build has a sample in
/// <see cref="GovernanceEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class GovernanceEmailsTests
{
    private static readonly LocalDateTime ClosesAt = new(2026, 4, 1, 18, 0);

    private static GovernanceEmails Create() => TestGovernanceEmails.Create();

    [HumansFact]
    public void ApplicationDecisions_StampGovernanceCategory()
    {
        var emails = Create();

        var approved = emails.ApplicationApproved("a@x.com", "Alice", MembershipTier.Colaborador, "en");
        approved.RecipientEmail.Should().Be("a@x.com");
        approved.RecipientName.Should().Be("Alice");
        approved.TemplateName.Should().Be("application_approved");
        approved.Category.Should().Be(MessageCategory.Governance);
        approved.ReplyTo.Should().BeNull();

        var rejected = emails.ApplicationRejected("a@x.com", "Alice", MembershipTier.Asociado, "No", "en");
        rejected.TemplateName.Should().Be("application_rejected");
        rejected.Category.Should().Be(MessageCategory.Governance);
    }

    [HumansFact]
    public void TermRenewalReminder_StampsGovernanceCategory()
    {
        var msg = Create().TermRenewalReminder("a@x.com", "Alice", "Colaborador", "April 1, 2026", "en");

        msg.TemplateName.Should().Be("term_renewal_reminder");
        msg.Category.Should().Be(MessageCategory.Governance);
    }

    [HumansFact]
    public void AssemblyVoteMail_IsAlwaysSend_SystemCategory()
    {
        var emails = Create();

        var opened = emails.AssemblyVoteOpened("a@x.com", "Alice", "Budget", ClosesAt, true, "/Governance/Votes/1", "en");
        opened.TemplateName.Should().Be("assembly_vote_opened");
        opened.Category.Should().Be(MessageCategory.System);

        var reminder = emails.AssemblyVoteReminder("a@x.com", "Alice", "Budget", ClosesAt, false, "/Governance/Votes/1", "en");
        reminder.TemplateName.Should().Be("assembly_vote_reminder");
        reminder.Category.Should().Be(MessageCategory.System);

        var cancelled = emails.AssemblyVoteCancelled("a@x.com", "Alice", "Budget", "Withdrawn", "en");
        cancelled.TemplateName.Should().Be("assembly_vote_cancelled");
        cancelled.Category.Should().Be(MessageCategory.System);
    }

    [HumansFact]
    public void AssemblyVoteOpened_BuildsAnAbsoluteVoteLink()
    {
        var msg = Create().AssemblyVoteOpened("a@x.com", "Alice", "Budget", ClosesAt, true, "/Governance/Votes/1", "en");

        msg.HtmlBody.Should().Contain("https://humans.example/Governance/Votes/1");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new GovernanceEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Governance template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new GovernanceEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(GovernanceEmails emails) =>
    [
        .. typeof(GovernanceEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(bool)) return true;
        if (type == typeof(MembershipTier)) return MembershipTier.Colaborador;
        if (type == typeof(LocalDateTime)) return ClosesAt;
        throw new NotSupportedException(
            $"GovernanceEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
