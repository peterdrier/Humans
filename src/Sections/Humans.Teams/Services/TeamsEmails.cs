using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Teams.Services;

/// <summary>
/// Teams' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Teams chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Teams' own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class TeamsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TeamsResource> localizer,
    ILogger<TeamsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage AddedToTeam(string userEmail, string userName, string teamName, string teamSlug, IEnumerable<(string Name, string? Url)> resources, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            Lf("Teams_Email_AddedToTeam_Subject", teamName),
            Lf("Teams_Email_AddedToTeam_Body", Encode(userName), Encode(teamName),
                ResourcesSection(resources), $"{_settings.BaseUrl}/Teams/{teamSlug}"),
            "added_to_team", MessageCategory.TeamUpdates));

    /// <summary>The team's resource list — omitted entirely rather than rendered as an empty list.</summary>
    private string ResourcesSection(IEnumerable<(string Name, string? Url)> resources)
    {
        var items = resources.ToList();
        return items.Count > 0
            ? Lf("Teams_Email_ResourcesSection",
                string.Join("\n", items.Select(r =>
                    !string.IsNullOrEmpty(r.Url)
                        ? $"<li><a href=\"{r.Url}\">{Encode(r.Name)}</a></li>"
                        : $"<li>{Encode(r.Name)}</li>")))
            : "";
    }

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build();
        }
    }
}
