using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;

namespace Humans.Calendar.Services;

/// <summary>
/// GDPR fan-out contributor for Calendar (nobodies-collective/Humans#1116). The only
/// user-scoped columns in <see cref="Data.CalendarDbContext"/> are the bare
/// <c>CreatedByUserId</c> foreign keys on <c>CalendarEvent</c> and
/// <c>CalendarEventException</c> — no name, email, or other denormalized personal data is
/// stored alongside them. <see cref="ContributeForUserAsync"/> always returns an empty
/// list: no read path exists for "events created by this user" today (the repository and
/// <see cref="ICalendarService"/> only load by event id or return everything for the cache
/// warmup), and one is not added solely to populate an export slice — see
/// <see cref="ErasureDeclaration"/> for why the FK itself is retained rather than erased.
/// </summary>
internal sealed class CalendarGdprContributor : IApplicationService, IUserDataContributor
{
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.CalendarEvents] =
                "Retained: a calendar event is the association's own record of when something " +
                "is scheduled, not the creator's personal data beyond the bare CreatedByUserId " +
                "attribution column. Users' own Article 17 erasure anonymizes the account that " +
                "id points at, so after erasure the row still names an event and a scheduler, " +
                "just an anonymized one rather than a named person."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// No-op: nothing on the Calendar side changes. The retained
    /// <c>CreatedByUserId</c> keeps pointing at the same row in Users, and it is that row's
    /// own erasure path that anonymizes it — Calendar has no personal data of its own left
    /// to touch once that has happened.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
