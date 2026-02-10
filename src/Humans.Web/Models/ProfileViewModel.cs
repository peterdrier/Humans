using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class ProfileViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }

    /// <summary>
    /// Whether the profile has a custom uploaded picture (takes precedence over Google avatar).
    /// </summary>
    public bool HasCustomProfilePicture { get; set; }

    /// <summary>
    /// URL to the custom profile picture endpoint (if uploaded).
    /// </summary>
    public string? CustomProfilePictureUrl { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Burner Name")]
    public string BurnerName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal Last Name")]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the viewer can see legal name (own profile or board member).
    /// </summary>
    public bool CanViewLegalName { get; set; }

    /// <summary>
    /// Whether the viewer is looking at their own profile.
    /// Controls visibility of edit buttons, quick actions, and owner-only UI.
    /// </summary>
    public bool IsOwnProfile { get; set; }

    [Display(Name = "Country Code")]
    [StringLength(5)]
    [RegularExpression(@"^\+\d{1,4}$", ErrorMessage = "Please enter a valid country code (e.g., +34, +1)")]
    public string? PhoneCountryCode { get; set; }

    [Display(Name = "Phone Number")]
    [StringLength(20)]
    [RegularExpression(@"^[\d\s\-]+$", ErrorMessage = "Please enter a valid phone number")]
    public string? PhoneNumber { get; set; }

    [StringLength(256)]
    public string? City { get; set; }

    [Display(Name = "Country")]
    [StringLength(2)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// Latitude coordinate from Google Places.
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate from Google Places.
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Google Places ID for future reference.
    /// </summary>
    [StringLength(512)]
    public string? PlaceId { get; set; }

    /// <summary>
    /// Display-friendly location string for the autocomplete input.
    /// </summary>
    public string? LocationDisplay => !string.IsNullOrEmpty(City) && !string.IsNullOrEmpty(CountryCode)
        ? $"{City}, {CountryCode}"
        : City ?? CountryCode;

    [StringLength(1000)]
    [DataType(DataType.MultilineText)]
    public string? Bio { get; set; }

    /// <summary>
    /// Date of birth as a string for the date input (yyyy-MM-dd format).
    /// </summary>
    [Display(Name = "Date of Birth")]
    public string? DateOfBirthString { get; set; }

    /// <summary>
    /// Parsed date of birth. Returns null if invalid.
    /// </summary>
    public LocalDate? ParsedDateOfBirth
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateOfBirthString))
                return null;

            var pattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("yyyy-MM-dd");
            var parseResult = pattern.Parse(DateOfBirthString);
            return parseResult.Success ? parseResult.Value : null;
        }
    }

    /// <summary>
    /// Profile picture file upload (max 2MB, JPEG/PNG).
    /// </summary>
    [Display(Name = "Profile Picture")]
    public IFormFile? ProfilePictureUpload { get; set; }

    /// <summary>
    /// Whether to remove the current custom profile picture.
    /// </summary>
    public bool RemoveProfilePicture { get; set; }

    public string MembershipStatus { get; set; } = "None";
    public bool IsApproved { get; set; }
    public bool HasPendingConsents { get; set; }
    public int PendingConsentCount { get; set; }

    /// <summary>
    /// The effective profile picture URL (custom upload takes priority over Google avatar).
    /// </summary>
    public string? EffectiveProfilePictureUrl => HasCustomProfilePicture
        ? CustomProfilePictureUrl
        : ProfilePictureUrl;

    /// <summary>
    /// Formatted date of birth for display.
    /// </summary>
    public string? FormattedDateOfBirth
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateOfBirthString))
                return null;

            var parsed = ParsedDateOfBirth;
            if (parsed == null)
                return null;

            var pattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("MMMM d");
            return pattern.Format(parsed.Value);
        }
    }

    /// <summary>
    /// Gets the formatted phone number with country code.
    /// </summary>
    public string? FormattedPhoneNumber =>
        !string.IsNullOrEmpty(PhoneCountryCode) && !string.IsNullOrEmpty(PhoneNumber)
            ? $"{PhoneCountryCode} {PhoneNumber}"
            : PhoneNumber;

    /// <summary>
    /// Contact fields visible to the current viewer (for display).
    /// </summary>
    public IReadOnlyList<ContactFieldViewModel> ContactFields { get; set; } = [];

    /// <summary>
    /// Contact fields for editing (owner only).
    /// </summary>
    public List<ContactFieldEditViewModel> EditableContactFields { get; set; } = [];

    /// <summary>
    /// Volunteer history entries for display.
    /// </summary>
    public IReadOnlyList<VolunteerHistoryEntryViewModel> VolunteerHistory { get; set; } = [];

    /// <summary>
    /// Volunteer history entries for editing (owner only).
    /// </summary>
    public List<VolunteerHistoryEntryEditViewModel> EditableVolunteerHistory { get; set; } = [];

    /// <summary>
    /// Teams the user is a member of (excluding Volunteers system team).
    /// </summary>
    public IReadOnlyList<TeamMembershipViewModel> Teams { get; set; } = [];
}

/// <summary>
/// Team membership for display purposes.
/// </summary>
public class TeamMembershipViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool IsMetalead { get; set; }
    public bool IsSystemTeam { get; set; }
}

/// <summary>
/// Contact field for display purposes.
/// </summary>
public class ContactFieldViewModel
{
    public Guid Id { get; set; }
    public ContactFieldType FieldType { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ContactFieldVisibility Visibility { get; set; }

    /// <summary>
    /// Gets a Font Awesome icon class for this field type.
    /// </summary>
    public string IconClass => FieldType switch
    {
        ContactFieldType.Email => "fa-solid fa-envelope",
        ContactFieldType.Phone => "fa-solid fa-phone",
        ContactFieldType.Signal => "fa-solid fa-comment-dots",
        ContactFieldType.Telegram => "fa-brands fa-telegram",
        ContactFieldType.WhatsApp => "fa-brands fa-whatsapp",
        _ => "fa-solid fa-address-card"
    };

    /// <summary>
    /// Gets a visibility icon class.
    /// </summary>
    public string VisibilityIconClass => Visibility switch
    {
        ContactFieldVisibility.BoardOnly => "fa-solid fa-lock",
        ContactFieldVisibility.LeadsAndBoard => "fa-solid fa-user-shield",
        ContactFieldVisibility.MyTeams => "fa-solid fa-users",
        ContactFieldVisibility.AllActiveProfiles => "fa-solid fa-globe",
        _ => "fa-solid fa-eye"
    };

    /// <summary>
    /// Gets a visibility tooltip.
    /// </summary>
    public string VisibilityTooltip => Visibility switch
    {
        ContactFieldVisibility.BoardOnly => "Visible to board members only",
        ContactFieldVisibility.LeadsAndBoard => "Visible to team leads and board",
        ContactFieldVisibility.MyTeams => "Visible to members of your teams",
        ContactFieldVisibility.AllActiveProfiles => "Visible to all active members",
        _ => "Visibility unknown"
    };
}

/// <summary>
/// Contact field for editing purposes.
/// </summary>
public class ContactFieldEditViewModel : IValidatableObject
{
    public Guid? Id { get; set; }

    [Required]
    public ContactFieldType FieldType { get; set; }

    [StringLength(100)]
    [Display(Name = "Custom Label")]
    public string? CustomLabel { get; set; }

    [Required]
    [StringLength(500)]
    public string Value { get; set; } = string.Empty;

    [Required]
    public ContactFieldVisibility Visibility { get; set; } = ContactFieldVisibility.AllActiveProfiles;

    public int DisplayOrder { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FieldType == ContactFieldType.Email && !string.IsNullOrWhiteSpace(Value))
        {
            if (!new EmailAddressAttribute().IsValid(Value))
            {
                yield return new ValidationResult(
                    "Please enter a valid email address.",
                    [nameof(Value)]);
            }
        }
    }
}

/// <summary>
/// Volunteer history entry for display purposes.
/// </summary>
public class VolunteerHistoryEntryViewModel
{
    public Guid Id { get; set; }
    public LocalDate Date { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Gets the date formatted as "Mon'YY" (e.g., "Mar'25").
    /// </summary>
    public string FormattedDate
    {
        get
        {
            var pattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("MMM");
            var yearPattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("yy");
            return $"{pattern.Format(Date)}'{yearPattern.Format(Date)}";
        }
    }
}

/// <summary>
/// Volunteer history entry for editing purposes.
/// </summary>
public class VolunteerHistoryEntryEditViewModel
{
    public Guid? Id { get; set; }

    [Required]
    [Display(Name = "Date")]
    public string DateString { get; set; } = string.Empty;

    [Required]
    [StringLength(256)]
    [Display(Name = "Event Name")]
    public string EventName { get; set; } = string.Empty;

    [StringLength(2000)]
    [DataType(DataType.MultilineText)]
    public string? Description { get; set; }

    /// <summary>
    /// Parses DateString to LocalDate. Returns null if invalid.
    /// </summary>
    public LocalDate? ParsedDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DateString))
                return null;

            // Try parsing as yyyy-MM-dd (HTML date input format)
            var pattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("yyyy-MM-dd");
            var parseResult = pattern.Parse(DateString);
            if (parseResult.Success)
                return parseResult.Value;

            // Try parsing as yyyy-MM (month input format) - use first of month
            if (DateString.Length == 7)
            {
                var monthPattern = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("yyyy-MM-dd");
                var monthResult = monthPattern.Parse(DateString + "-01");
                if (monthResult.Success)
                    return monthResult.Value;
            }

            return null;
        }
    }
}

/// <summary>
/// View model for the privacy/data management page.
/// </summary>
public class PrivacyViewModel
{
    public bool IsDeletionPending { get; set; }
    public DateTime? DeletionRequestedAt { get; set; }
    public DateTime? DeletionScheduledFor { get; set; }
}
