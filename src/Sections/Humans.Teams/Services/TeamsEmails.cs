using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Teams.Services;

internal sealed class TeamsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TeamsResource> localizer,
    ILogger<TeamsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage AddedToTeam(Guid userId, string userEmail, string userName, string teamName, string teamSlug, IEnumerable<(string Name, string? Url)> resources, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName, userId,
            localizer["Teams_Email_AddedToTeam_Subject", teamName],
            localizer["Teams_Email_AddedToTeam_Body", Encode(userName), Encode(teamName),
                ResourcesSection(resources), Encode($"{_settings.BaseUrl.TrimEnd('/')}/Teams/{teamSlug}")],
            "added_to_team", MessageCategory.TeamUpdates));

    /// <summary>Omitted entirely rather than rendered as an empty list.</summary>
    private string ResourcesSection(IEnumerable<(string Name, string? Url)> resources)
    {
        var items = resources.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        return items.Count > 0
            ? localizer["Teams_Email_ResourcesSection",
                string.Join("\n", items.Select(r =>
                    !string.IsNullOrEmpty(r.Url)
                        ? $"<li><a href=\"{Encode(r.Url)}\">{Encode(r.Name)}</a></li>"
                        : $"<li>{Encode(r.Name)}</li>"))]
            : "";
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build() with { Culture = CultureInfo.CurrentUICulture.Name };
        }
    }
}
