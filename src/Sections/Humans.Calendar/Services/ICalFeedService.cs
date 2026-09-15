using Humans.Calendar.Contracts;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Humans.Users.Contracts;

namespace Humans.Calendar.Services;

/// <summary>
/// Orchestrator: fans the personal iCal feed out across <see cref="ICalendarFeedContributor"/>s
/// into one VCALENDAR. Sequential, not Task.WhenAll — not for safety (each contributor's
/// factory-created context is safe to run concurrently, design-rules §8a) but for consistency
/// with the other contributor fan-outs.
/// </summary>
internal sealed class ICalFeedService(
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
        // #1704: the read resolves merges forward, so a tombstone id answers with the survivor.
        // Comparing the row back against the requested id keeps the documented contract that a
        // merged user's feed is a 404 — no oracle telling the holder of an old URL who absorbed
        // the account, and no feed served under an id that no longer names a human.
        var user = await users.GetUserInfoAsync(userId, ct);
        if (user is null || user.Id != userId || user.ICalToken is null || user.ICalToken.Value != token)
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
