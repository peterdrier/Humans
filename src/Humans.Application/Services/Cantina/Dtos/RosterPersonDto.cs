using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// One human on the Cantina Weekly Roster. Deliberately excludes
/// <c>MedicalConditions</c>: medical fields never cross the Application
/// boundary in the roster surface. The volunteer's <see cref="BurnerName"/>
/// is stitched in by the service layer via <c>IProfileService</c>
/// (cross-section read; no nav-property include).
/// </summary>
/// <param name="UserId">The human's user id.</param>
/// <param name="BurnerName">
/// Display label, sourced from the human's profile <c>BurnerName</c>;
/// falls back to the user's <c>DisplayName</c> if no profile / burner
/// name is set, and finally to <c>"(unknown)"</c> if neither resolves.
/// </param>
/// <param name="DaysOnSite">
/// Calendar dates within the requested week on which this human had a
/// Pending/Confirmed signup. Ordered Mon..Sun ascending.
/// </param>
/// <param name="DietaryPreference">
/// One of the canonical preferences in
/// <see cref="Humans.Domain.Constants.DietaryOptions.DietaryPreferences"/>,
/// or null/empty if the human has not answered yet (counted as "Unanswered").
/// </param>
/// <param name="Allergies">
/// Canonical allergy chips the human ticked. Free-text from the
/// "Other" chip is in <see cref="AllergyOtherText"/>.
/// </param>
/// <param name="AllergyOtherText">Free-text follow-up when "Other" was checked.</param>
/// <param name="Intolerances">Same shape as <see cref="Allergies"/> but for intolerances.</param>
/// <param name="IntoleranceOtherText">Free-text follow-up when "Other" was checked.</param>
public sealed record RosterPersonDto(
    Guid UserId,
    string BurnerName,
    IReadOnlyList<LocalDate> DaysOnSite,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText);
