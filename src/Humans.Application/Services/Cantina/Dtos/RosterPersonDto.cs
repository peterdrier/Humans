using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// One human on the Cantina Weekly Roster. Deliberately excludes
/// <c>MedicalConditions</c>: medical fields never cross the Application
/// boundary in the roster surface. The volunteer's <see cref="BurnerName"/>
/// is stitched in by the service layer via <c>IProfileService</c>
/// (cross-section read; no nav-property include).
///
/// Cohort invariant: users with no Pending/Confirmed signups for the week
/// are excluded from the cohort entirely (see
/// <see cref="WeeklyRosterDto.People"/>). Every <see cref="RosterPersonDto"/>
/// therefore has at least one on-site day, which makes
/// <see cref="ArrivesOn"/> non-nullable by construction.
/// </summary>
/// <param name="UserId">The human's user id.</param>
/// <param name="BurnerName">
/// Display label, sourced from the human's profile <c>BurnerName</c>;
/// falls back to the user's <c>DisplayName</c> if no profile / burner
/// name is set, and finally to <c>"(unknown)"</c> if neither resolves.
/// </param>
/// <param name="ArrivesOn">
/// Earliest calendar date within the requested week on which this human
/// had a Pending/Confirmed signup. Non-nullable: every person in the cohort
/// has at least one on-site day by definition.
/// </param>
/// <param name="NoShift">
/// Calendar dates within the requested week range on which this human had
/// NO signup — the complement of their on-site days within the 7-day week.
/// Empty when the human has a scheduled shift every day of the week. Sorted
/// ascending. Renamed from <c>DaysOff</c>: "off" suggested "off-site", but
/// the cantina semantic is "no scheduled shift" — the human could still be
/// on-site (at barrio, working informally) or off-event entirely.
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
    LocalDate ArrivesOn,
    IReadOnlyList<LocalDate> NoShift,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText);
