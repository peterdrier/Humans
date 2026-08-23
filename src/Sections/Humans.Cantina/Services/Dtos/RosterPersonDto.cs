using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// One human on the Cantina Weekly Roster. Deliberately excludes
/// <c>MedicalConditions</c>: the cached profile the service reads does carry
/// it, and this record is where it stops. The volunteer's <see cref="BurnerName"/>
/// is stitched in by the service layer from <c>IUserServiceRead</c>'s cached
/// profile read-model.
///
/// Cohort invariant: users with no confirmed signup and no arrival day in the
/// week are excluded from the cohort entirely (see
/// <see cref="WeeklyRosterDto.People"/>). Every <see cref="RosterPersonDto"/>
/// therefore has at least one on-site day, which makes
/// <see cref="ArrivesOn"/> non-nullable by construction.
/// </summary>
/// <param name="UserId">The human's user id.</param>
/// <param name="BurnerName">
/// Display label, sourced from the human's profile <c>BurnerName</c>.
/// <c>"(unknown)"</c> is a defensive default for a missing profile row, not
/// a case the roster is expected to render — every on-site human has one.
/// </param>
/// <param name="ArrivesOn">
/// Earliest calendar date within the requested week on which this human was
/// on site — a confirmed signup, or their arrival day. Equals the human's
/// true arrival only when they arrive during this week; for a multi-week
/// attendee it is simply their first day in the window. Non-nullable: every
/// person in the cohort has at least one on-site day by definition.
/// </param>
/// <param name="NoShift">
/// Calendar dates within the requested week range on which this human had
/// NO signup — the complement of their on-site days within the 7-day week.
/// Empty when the human has a scheduled shift every day of the week. Sorted
/// ascending. "No shift" is not "off site": the human may well be around
/// that day, working informally or at barrio.
/// </param>
/// <param name="DietaryPreference">
/// One of the canonical preferences in
/// <see cref="Humans.Users.Contracts.DietaryOptions.DietaryPreferences"/>,
/// or null/empty if the human has not answered yet (counted as "Unanswered").
/// </param>
/// <param name="Allergies">
/// Canonical allergy chips the human ticked. Free-text from the
/// "Other" chip is in <see cref="AllergyOtherText"/>.
/// </param>
/// <param name="AllergyOtherText">Free-text follow-up when "Other" was checked.</param>
/// <param name="Intolerances">Same shape as <see cref="Allergies"/> but for intolerances.</param>
/// <param name="IntoleranceOtherText">Free-text follow-up when "Other" was checked.</param>
internal sealed record RosterPersonDto(
    Guid UserId,
    string BurnerName,
    LocalDate ArrivesOn,
    IReadOnlyList<LocalDate> NoShift,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText);
