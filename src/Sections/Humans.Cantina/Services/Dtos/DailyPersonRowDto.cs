namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// One human's row in the Cantina Daily Matrix. Carries the canonical chip
/// selections as <see cref="IReadOnlySet{T}"/>s so the matrix render loop
/// in the view can do O(1) <c>Contains</c> lookups per cell. Mirrors the
/// shape of <see cref="RosterPersonDto"/> but without the week-scoped
/// <c>ArrivesOn</c>/<c>NoShift</c> fields (the daily view is a single day,
/// every row is on-site that day by definition).
///
/// Deliberately excludes <c>MedicalConditions</c>: the cached profile the
/// service reads does carry it, and this record is where it stops (GDPR
/// Art. 9 boundary; same rule as <see cref="RosterPersonDto"/>).
/// </summary>
/// <param name="UserId">The human's user id.</param>
/// <param name="BurnerName">
/// Display label, sourced from the human's profile <c>BurnerName</c>.
/// <c>"(unknown)"</c> is a defensive default for a missing profile row, not
/// a case the matrix is expected to render — every on-site human has one.
/// </param>
/// <param name="DietaryPreference">
/// One of the canonical preferences in
/// <see cref="Humans.Users.Contracts.DietaryOptions.DietaryPreferences"/>,
/// or null/empty if the human has not answered yet.
/// </param>
/// <param name="Allergies">
/// Canonical allergy chip labels the human ticked. <see cref="IReadOnlySet{T}"/>
/// (not list) so the matrix view can do per-column O(1) hit-testing.
/// </param>
/// <param name="AllergyOtherText">Free-text follow-up when "Other" was checked.</param>
/// <param name="Intolerances">Same shape as <see cref="Allergies"/> but for intolerances.</param>
/// <param name="IntoleranceOtherText">Free-text follow-up when "Other" was checked.</param>
internal sealed record DailyPersonRowDto(
    Guid UserId,
    string BurnerName,
    string? DietaryPreference,
    IReadOnlySet<string> Allergies,
    string? AllergyOtherText,
    IReadOnlySet<string> Intolerances,
    string? IntoleranceOtherText);
