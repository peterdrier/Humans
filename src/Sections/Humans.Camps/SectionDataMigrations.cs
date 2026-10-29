using Humans.Base.Interfaces;
using Humans.Camps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Camps;

/// <summary>
/// Camps' data migrations, run from <c>/Debug/DataMigrations</c>. The Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string EventIdsKey = "camps.event-ids";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(EventIdsKey, "Camps",
            "Give every barrio season the event of its year, and turn the open-season years into per-event registration rows",
            "The open-season year list on camp_settings, and seasons without an event id",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Season", "Name", "Year", "Event"],
            [.. (await sp.GetRequiredService<ICampManagement>().GetUnassignedSeasonsAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.SeasonId.ToString(),
                    p.SeasonName,
                    p.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.EventName])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ICampManagement>().AssignSeasonEventIdsAsync(ct);
}
