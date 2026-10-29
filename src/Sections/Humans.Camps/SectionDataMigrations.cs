using System.Globalization;
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
            "The open-season year list on camp_settings (emptied by this run), and seasons without an event id",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct)
    {
        var plan = await sp.GetRequiredService<ICampManagement>().GetEventIdPlanAsync(ct);
        return new(["Season", "Name", "Year", "Event"],
            [
                .. plan.LegacyOpenYears.Select(year => (IReadOnlyList<string>)[
                    "(open-season year)", "", year.ToString(CultureInfo.InvariantCulture), "Pinned to its events, then removed"]),
                .. plan.Seasons.Select(p => (IReadOnlyList<string>)[
                    p.SeasonId.ToString(),
                    p.SeasonName,
                    p.Year.ToString(CultureInfo.InvariantCulture),
                    p.EventName]),
            ]);
    }

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ICampManagement>().AssignSeasonEventIdsAsync(ct);
}
