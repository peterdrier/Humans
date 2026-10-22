using Humans.Calendar.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Calendar.Services;

/// <summary>Sequential only for consistency with the other contributor fan-outs; concurrency would be safe.</summary>
internal sealed class ICalFeedService(
    IUserService users,
    ICalendarFeedTokenService tokens,
    IEnumerable<ICalendarFeedContributor> contributors,
    IOptions<EmailSettings> emailSettings,
    ILogger<ICalFeedService> logger) : IICalFeedService
{
    public async Task<IReadOnlyList<CalendarFeedItem>> GetFeedItemsAsync(Guid userId, CancellationToken ct = default)
    {
        var items = new List<CalendarFeedItem>();
        foreach (var contributor in contributors)
        {
            IReadOnlyList<CalendarFeedItem> contributed;
            try
            {
                contributed = await contributor.GetCalendarItemsForUserAsync(userId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never swallow: a silently missing section would look like the member has no commitments there.
                logger.LogError(
                    ex,
                    "iCal feed contributor {Contributor} failed for user {UserId}",
                    contributor.GetType().Name,
                    userId);
                throw;
            }

            items.AddRange(contributed);
        }

        return items.OrderBy(i => i.Start).ToList();
    }

    public async Task<bool> HasFeedAsync(Guid userId, CancellationToken ct = default) =>
        await tokens.GetAsync(userId, ct) is not null;

    public async Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        // The read resolves merges forward; comparing ids keeps a merged user's feed a 404, so an old URL
        // reveals nothing about who absorbed the account.
        var user = await users.GetUserInfoAsync(userId, ct);
        if (user is null || user.Id != userId)
        {
            return null;
        }

        if (await tokens.GetAsync(userId, ct) is not { } stored || stored != token)
        {
            return null;
        }

        using var ownerCulture = new CultureScope(user.PreferredLanguage, logger);
        var items = await GetFeedItemsAsync(userId, ct);

        var calendar = new Ical.Net.Calendar { ProductId = "-//Nobodies Collective//Humans//EN" };
        calendar.AddProperty("X-WR-CALNAME", "Nobodies");

        foreach (var item in items)
        {
            calendar.Events.Add(new CalendarEvent
            {
                Uid = item.Uid,
                Summary = item.Summary,
                Description = item.Description,
                Location = item.Location,
                // ToDateTimeUtc keeps Kind=Utc so DTSTART gets its Z suffix (Ical.Net version-sensitive; a test guards it).
                DtStart = new CalDateTime(item.Start.ToDateTimeUtc()),
                DtEnd = new CalDateTime(item.End.ToDateTimeUtc()),
                Categories = [item.Source],
                Url = item.Url is null ? null : new Uri(BuildAbsoluteUrl(item.Url)),
            });
        }

        return new CalendarSerializer().SerializeToString(calendar);
    }

    private string BuildAbsoluteUrl(string path) =>
        $"{emailSettings.Value.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
