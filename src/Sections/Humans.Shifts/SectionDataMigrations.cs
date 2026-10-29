using Humans.Base.Interfaces;
using Humans.Shifts.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts;

/// <summary>
/// Shifts' data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves Shifts' own service; the
/// Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string BuildSubPeriodsKey = "shifts.build-sub-periods";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(BuildSubPeriodsKey, "Shifts",
            "Copy each event's build sub-period starts (first crew, set-up week, pre-event week, finishing weekend) from Settings into Shifts' event_settings",
            "EventCalendarResolver's fallback to Settings' sub-period values; then Settings stops carrying the four offsets",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Event", "Name", "First crew", "Set-up week", "Pre-event week", "Finishing weekend"],
            [.. (await sp.GetRequiredService<IShiftManagement>().GetUncopiedBuildSubPeriodsAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.EventSettingsId.ToString(),
                    p.EventName,
                    p.Starts.FirstCrew.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.SetupWeek.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.PreEventWeek.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.FinishingWeekend.ToString(System.Globalization.CultureInfo.InvariantCulture)])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IShiftManagement>().CopyBuildSubPeriodsAsync(ct);
}
