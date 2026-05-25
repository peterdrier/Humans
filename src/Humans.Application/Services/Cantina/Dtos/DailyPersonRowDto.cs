namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// One human's row in the Cantina Daily Matrix. Carries the canonical chip
/// selections as <see cref="IReadOnlySet{T}"/>s so the matrix render loop
/// in the view can do O(1) <c>Contains</c> lookups per cell. Mirrors the
/// shape of <see cref="RosterPersonDto"/> but without the week-scoped
/// <c>ArrivesOn</c>/<c>NoShift</c> fields (the daily view is a single day,
/// every row is on-site that day by definition).
///
/// Deliberately excludes <c>MedicalConditions</c>: medical fields never
/// cross the Application boundary in the roster surface (GDPR Art. 9
/// boundary; same rule as <see cref="RosterPersonDto"/>).
/// </summary>
/// <param name="UserId">The human's user id.</param>
/// <param name="BurnerName">
/// Display label, sourced from the human's profile <c>BurnerName</c>;
/// falls back to the user's <c>DisplayName</c> if no profile / burner
/// name is set, and finally to <c>"(unknown)"</c> if neither resolves.
/// </param>
/// <param name="DietaryPreference">
/// One of the canonical preferences in
/// <see cref="Humans.Domain.Constants.DietaryOptions.DietaryPreferences"/>,
/// or null/empty if the human has not answered yet.
/// </param>
/// <param name="Allergies">
/// Canonical allergy chip labels the human ticked. <see cref="IReadOnlySet{T}"/>
/// (not list) so the matrix view can do per-column O(1) hit-testing.
/// </param>
/// <param name="AllergyOtherText">Free-text follow-up when "Other" was checked.</param>
/// <param name="Intolerances">Same shape as <see cref="Allergies"/> but for intolerances.</param>
/// <param name="IntoleranceOtherText">Free-text follow-up when "Other" was checked.</param>
public sealed record DailyPersonRowDto(
    Guid UserId,
    string BurnerName,
    string? DietaryPreference,
    IReadOnlySet<string> Allergies,
    string? AllergyOtherText,
    IReadOnlySet<string> Intolerances,
    string? IntoleranceOtherText);
