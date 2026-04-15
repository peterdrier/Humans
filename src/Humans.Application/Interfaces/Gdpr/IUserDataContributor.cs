namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// Contributes one or more sections of the GDPR Article 15 data export for a
/// single user.
///
/// <para>
/// Every service that owns user-scoped tables MUST implement this interface so
/// the orchestrator (<see cref="IGdprExportService"/>) can fan out and assemble
/// a complete per-user document without any cross-section database reads.
/// A contributor reads only from its owning section's tables — cross-section
/// data flows through other contributors, not through <c>Include</c> chains.
/// </para>
///
/// <para>
/// Some contributors own several user-scoped tables and therefore emit several
/// top-level sections (for example <c>ShiftSignupService</c> returns
/// <c>ShiftSignups</c>, <c>VolunteerEventProfiles</c>, <c>GeneralAvailability</c>,
/// and <c>ShiftTagPreferences</c>). Returning a list keeps those stable JSON
/// top-level keys the user sees in the export file.
/// </para>
/// </summary>
public interface IUserDataContributor
{
    /// <summary>
    /// Returns every personal-data slice this contributor owns for
    /// <paramref name="userId"/>. Implementations must be read-only.
    /// A slice whose <see cref="UserDataSlice.Data"/> is <c>null</c> is dropped
    /// from the final export by the orchestrator.
    /// </summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);
}
