using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Users;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.ICalFeed;

/// <summary>
/// Orchestrator: fans the personal iCal feed out across <see cref="ICalendarFeedContributor"/>s
/// into one VCALENDAR. Sequential, not Task.WhenAll: contributors share the
/// scoped HumansDbContext which is not thread-safe (same as GdprExportService).
/// </summary>
public sealed class ICalFeedService(
    IUserServiceRead users,
    IEnumerable<ICalendarFeedContributor> contributors,
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
            catch (Exception ex)
            {
                // Never swallow: silently omitting a section's items would look
                // like the user has no commitments there.
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

    public async Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        if (user is null || user.MergedToUserId is not null
            || user.ICalToken is null || user.ICalToken.Value != token)
        {
            return null;
        }

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
                // ToDateTimeUtc() keeps Kind=Utc so DTSTART serializes with a Z suffix
                // (version-sensitive Ical.Net behavior — the DTSTART:...Z test assertion
                // guards it; fallback is the CalDateTime(DateTime, "UTC") overload).
                DtStart = new CalDateTime(item.Start.ToDateTimeUtc()),
                DtEnd = new CalDateTime(item.End.ToDateTimeUtc()),
                Categories = [item.Source],
                Url = item.Url is null ? null : new Uri(item.Url),
            });
        }

        return new CalendarSerializer().SerializeToString(calendar);
    }
}
