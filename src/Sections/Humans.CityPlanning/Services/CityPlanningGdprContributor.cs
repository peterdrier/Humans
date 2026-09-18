using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;

namespace Humans.CityPlanning.Services;

/// <summary>
/// GDPR fan-out contributor for City Planning (nobodies-collective/Humans#1116). The only
/// user-scoped columns in <see cref="Data.CityPlanningDbContext"/> are the bare
/// <c>LastModifiedByUserId</c> FK on <c>CampPolygon</c> and <c>ModifiedByUserId</c> on
/// <c>CampPolygonHistory</c> — no name, email, or other denormalized personal data sits
/// alongside them. <c>CampPolygonHistory.Note</c> is free text, but it describes the edit
/// itself ("Saved", "Restored from {timestamp} UTC", an admin's own import label), not a
/// person, so it is not treated as personal data in its own right; see
/// <see cref="ErasureDeclaration"/> for why the FK it sits next to is retained rather than
/// erased. <see cref="ContributeForUserAsync"/> always returns an empty list: no read path
/// exists for "polygons or history rows edited by this user" today (the repository only
/// loads by camp season id), and one is not added solely to populate an export slice.
/// </summary>
internal sealed class CityPlanningGdprContributor : IApplicationService, IUserDataContributor
{
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.CityPlanningEdits] =
                "Retained: a camp polygon and its history are the association's own record of " +
                "how the city plan changed, not the editor's personal data beyond the bare " +
                "LastModifiedByUserId/ModifiedByUserId attribution columns. Users' own Article " +
                "17 erasure anonymizes the account those ids point at, so after erasure the " +
                "rows still name an edit and an editor, just an anonymized one rather than a " +
                "named person."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// No-op: nothing on the City Planning side changes. The retained
    /// <c>LastModifiedByUserId</c>/<c>ModifiedByUserId</c> columns keep pointing at the same
    /// row in Users, and it is that row's own erasure path that anonymizes it. City Planning
    /// has no personal data of its own left to touch once that has happened.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
