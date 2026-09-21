using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Teams.Services;
using Humans.Teams.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The per-template routing policy Teams stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="TeamsEmails"/> can build has a sample in
/// <see cref="TeamsEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class TeamsEmailsTests
{
    private static TeamsEmails Create() => TestTeamsEmails.Create();

    [HumansFact]
    public void AddedToTeam_StampsTeamUpdates()
    {
        var msg = Create().AddedToTeam("a@x.com", "Alice", "Alpha", "alpha", [], "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be("added_to_team");
        msg.Category.Should().Be(MessageCategory.TeamUpdates);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void AddedToTeam_LinksTheTeamAndOmitsAnEmptyResourceList()
    {
        var msg = Create().AddedToTeam("a@x.com", "Alice", "Alpha", "alpha", [], "en");

        msg.HtmlBody.Should().Contain($"{TestTeamsEmails.BaseUrl}/Teams/alpha");
        msg.HtmlBody.Should().NotContain("Resources:");
    }

    [HumansFact]
    public void AddedToTeam_RendersResourcesWithAndWithoutLinks()
    {
        var msg = Create().AddedToTeam("a@x.com", "Alice", "Alpha", "alpha",
            [("Drive", "https://drive.example/x"), ("Handbook", null)], "en");

        msg.HtmlBody.Should().Contain("<li><a href=\"https://drive.example/x\">Drive</a></li>");
        msg.HtmlBody.Should().Contain("<li>Handbook</li>");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new TeamsEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Teams template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new TeamsEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(TeamsEmails emails) =>
    [
        .. typeof(TeamsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(IEnumerable<(string Name, string? Url)>)) return Array.Empty<(string, string?)>();
        throw new NotSupportedException(
            $"TeamsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
