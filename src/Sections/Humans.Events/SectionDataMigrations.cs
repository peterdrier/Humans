using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Events.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Events;

/// <summary>
/// Event guide data migrations, run from <c>/Debug/DataMigrations</c>. The Debug host lists, applies and audits the
/// run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string EventIdsKey = "events.event-ids";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(EventIdsKey, "Events",
            "Give every guide entry the event it belongs to: the event whose build-to-strike window holds its start date, else the event with the nearest gate opening",
            "Treating guide entries without an event id as part of the guide's current event",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Entry", "Title", "Starts (UTC)", "Event"],
            [.. (await sp.GetRequiredService<IEventManagement>().GetUnassignedGuideEntriesAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.EntryId.ToString(),
                    p.Title,
                    p.StartAt.ToDateTime(DateTimeZone.Utc),
                    p.EventName])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IEventManagement>().AssignGuideEntryEventIdsAsync(ct);
}
