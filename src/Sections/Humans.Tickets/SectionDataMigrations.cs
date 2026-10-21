using System.Globalization;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Tickets.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets;

/// <summary>
/// Tickets' data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves Tickets' own
/// service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string EventParticipationsKey = "tickets.event-participations";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(EventParticipationsKey, "Tickets",
            "Copy every event-participation row from Users' event_participations into tickets_event_participations",
            "EventParticipationService's legacy fallback, its cleared rows, UserInfo.EventParticipations and the Legacy* participation enums; then a drop PR for event_participations",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Year", "Status", "Source", "Declared at", "Checked in at"],
            [.. (await sp.GetRequiredService<IEventParticipationManagement>().GetUncopiedLegacyParticipationsAsync(ct))
                .Select(p => (IReadOnlyList<string>)
                [
                    p.UserId.ToString(), p.Year.ToString(CultureInfo.InvariantCulture),
                    p.Status.ToString(), p.Source.ToString(),
                    p.DeclaredAt.ToIso8601() ?? string.Empty, p.CheckedInAt.ToIso8601() ?? string.Empty,
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IEventParticipationManagement>().CopyLegacyParticipationsAsync(ct);
}
