using Humans.Base.Interfaces;

namespace Humans.Gdpr.Contracts;

/// <summary>
/// Contributes one or more sections of the GDPR Article 15 data export for a
/// single user.
///
/// <para>
/// Every service that owns user-scoped tables MUST implement this interface so
/// the orchestrator (<see cref="IGdprService"/>) can fan out and assemble
/// a complete per-user document without any cross-section database reads.
/// A contributor reads only from its owning section's tables — cross-section
/// data flows through other contributors, not through <c>Include</c> chains.
/// </para>
///
/// <para>
/// Some contributors own several user-scoped tables and therefore emit several
/// top-level sections (for example <c>ShiftSignupService</c> returns
/// <c>ShiftSignups</c>, <c>VolunteerEventProfiles</c>, <c>GeneralAvailability</c>,
/// and <c>ShiftTagPreferences</c>). Returning a list keeps each one its own
/// top-level key in the export file.
/// </para>
/// </summary>
public interface IUserDataContributor : IFanout
{
    /// <summary>
    /// Returns every personal-data slice this contributor owns for
    /// <paramref name="userId"/>. Implementations must be read-only.
    /// A slice whose <see cref="UserDataSlice.Data"/> is <c>null</c> is dropped
    /// from the final export by the orchestrator. Every
    /// <see cref="UserDataSlice.SectionName"/> returned here must be a key of
    /// <see cref="ErasureDeclaration"/> — the orchestrator enforces this at
    /// export time — so a category a person can download is always accounted
    /// for under Article 17 too. The reverse isn't required: a contributor may
    /// declare erasure-only keys it never exports (see
    /// <see cref="ErasureDeclaration"/>).
    /// </summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Article 17 counterpart of <see cref="ContributeForUserAsync"/>: one entry
    /// for every export section name this contributor owns, plus any category it
    /// erases without ever exporting it. A <c>null</c> value means
    /// <see cref="EraseForUserAsync"/> erases or anonymizes that category
    /// in full; a non-empty value means something in it is deliberately kept, and
    /// the string says what survives and under which lawful basis.
    ///
    /// <para>
    /// MUST be a static table — the erasure-coverage architecture test reads it
    /// from an uninitialized instance, so an implementation may not touch
    /// instance state, the database, or the clock.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, string?> ErasureDeclaration { get; }

    /// <summary>
    /// Erases this contributor's personal data for <paramref name="userId"/>:
    /// every category <see cref="ErasureDeclaration"/> maps to <c>null</c> is
    /// deleted or anonymized in full, and every category mapped to a retention
    /// reason keeps exactly what that reason names. Implementations must be
    /// idempotent — the deletion job retries the whole cascade the next day
    /// after a mid-cascade failure.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// True for the one contributor that owns the person's identity/account
    /// record: it erases last, after every other contributor, so a section that
    /// still needs the person's address to reach an external processor (for
    /// example a Workspace suspend) can resolve it before identity collapses.
    /// The orchestrator orders erasure by this flag, not by naming a specific
    /// contributor type. Exactly one contributor should return <c>true</c>.
    /// </summary>
    bool ErasesLast => false;
}
