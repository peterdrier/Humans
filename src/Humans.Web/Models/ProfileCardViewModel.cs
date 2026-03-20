using NodaTime;
using Humans.Domain.Enums;
using Humans.Web.ViewComponents;

namespace Humans.Web.Models;

/// <summary>
/// View model for the ProfileCard ViewComponent.
/// </summary>
public class ProfileCardViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public bool HasCustomProfilePicture { get; set; }
    public string? CustomProfilePictureUrl { get; set; }
    public string BurnerName { get; set; } = string.Empty;
    public string? Pronouns { get; set; }
    public string MembershipStatus { get; set; } = "None";
    public bool IsApproved { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public int? BirthdayMonth { get; set; }
    public int? BirthdayDay { get; set; }
    public string? Bio { get; set; }
    public string? ContributionInterests { get; set; }
    public string? BoardNotes { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelationship { get; set; }
    public bool HasPendingConsents { get; set; }
    public int PendingConsentCount { get; set; }
    public ProfileCardViewMode ViewMode { get; set; }
    public bool CanViewLegalName { get; set; }
    public string? PreferredLanguage { get; set; }

    public IReadOnlyList<UserEmailDisplayViewModel> UserEmails { get; set; } = [];
    public IReadOnlyList<ContactFieldViewModel> ContactFields { get; set; } = [];
    public IReadOnlyList<VolunteerHistoryEntryViewModel> VolunteerHistory { get; set; } = [];
    public IReadOnlyList<TeamMembershipViewModel> Teams { get; set; } = [];

    public bool IsOwnProfile => ViewMode == ProfileCardViewMode.Self;

    /// <summary>
    /// The effective profile picture URL (custom upload takes priority over Google avatar).
    /// </summary>
    public string? EffectiveProfilePictureUrl => HasCustomProfilePicture
        ? CustomProfilePictureUrl
        : ProfilePictureUrl;

    /// <summary>
    /// Formatted birthday for display (e.g., "March 15").
    /// </summary>
    public string? FormattedBirthday
    {
        get
        {
            if (BirthdayMonth is not (>= 1 and <= 12) || BirthdayDay is not (>= 1 and <= 31))
                return null;

            try
            {
                var date = new LocalDate(4, BirthdayMonth.Value, BirthdayDay.Value);
                var pattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("MMMM d");
                return pattern.Format(date);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Non-BoardOnly emails for the public contact info section.
    /// </summary>
    /// <summary>
    /// Whether the viewer should see the "Send a message" button.
    /// True when viewing another user's profile and no email is visible.
    /// </summary>
    public bool CanSendMessage { get; set; }

    public IReadOnlyList<UserEmailDisplayViewModel> PublicUserEmails =>
        UserEmails.Where(e => e.Visibility != ContactFieldVisibility.BoardOnly).ToList();

    /// <summary>
    /// BoardOnly emails for the board/private section.
    /// </summary>
    public IReadOnlyList<UserEmailDisplayViewModel> BoardOnlyUserEmails =>
        UserEmails.Where(e => e.Visibility == ContactFieldVisibility.BoardOnly).ToList();
}
