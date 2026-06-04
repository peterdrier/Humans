using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Profiles;

public class PersonSearchMatcherTests
{
    private static readonly Instant At = Instant.FromUtc(2026, 1, 1, 0, 0);

    /// <summary>
    /// Builds a real <see cref="UserInfo"/> via <see cref="UserInfo.Create"/> (no mocks) so the
    /// resolved-name fallback and projections are exercised exactly as in production.
    /// </summary>
    private static UserInfo Human(
        string burnerName = "Sparkle",
        string firstName = "Test",
        string lastName = "Human",
        string displayName = "Test Human",
        string? city = null,
        string? bio = null,
        string? pronouns = null,
        string? interests = null,
        string? medicalConditions = null,
        string? adminNotes = null,
        string? boardNotes = null,
        string? iban = null,
        string? consentCheckNotes = null,
        string? rejectionReason = null,
        string? emergencyContactName = null,
        Instant? rejectedAt = null,
        IEnumerable<(string Value, ContactFieldVisibility Vis)>? contactFields = null,
        IEnumerable<(string Email, bool Verified, ContactFieldVisibility? Vis)>? emails = null,
        IEnumerable<(string EventName, string? Description)>? cv = null)
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = burnerName,
            FirstName = firstName,
            LastName = lastName,
            City = city,
            Bio = bio,
            Pronouns = pronouns,
            ContributionInterests = interests,
            MedicalConditions = medicalConditions,
            AdminNotes = adminNotes,
            BoardNotes = boardNotes,
            Iban = iban,
            ConsentCheckNotes = consentCheckNotes,
            RejectionReason = rejectionReason,
            EmergencyContactName = emergencyContactName,
            RejectedAt = rejectedAt,
            CreatedAt = At,
            UpdatedAt = At,
        };

        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = At,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };

        var contactEntities = (contactFields ?? [])
            .Select((c, i) => new ContactField
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                FieldType = ContactFieldType.Other,
                Value = c.Value,
                Visibility = c.Vis,
                DisplayOrder = i,
                CreatedAt = At,
                UpdatedAt = At,
            })
            .ToList();

        var emailEntities = (emails ?? [])
            .Select(e => new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = e.Email,
                IsVerified = e.Verified,
                Visibility = e.Vis,
                CreatedAt = At,
                UpdatedAt = At,
            })
            .ToList();

        var cvEntities = (cv ?? [])
            .Select(v => new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Date = new LocalDate(2025, 7, 1),
                EventName = v.EventName,
                Description = v.Description,
                CreatedAt = At,
                UpdatedAt = At,
            })
            .ToList();

        return UserInfo.Create(
            user: user,
            userEmails: emailEntities,
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: contactEntities,
            profileLanguages: [],
            volunteerHistory: cvEntities,
            communicationPreferences: []);
    }

    [HumansFact]
    public void Matches_resolved_display_name_when_burnername_blank()
    {
        // The reported bug: Profile.BurnerName blank, but legacy User.DisplayName populated
        // (SSO/legacy). The UI renders "Maria Garcia" via the resolver, yet search saw the raw
        // blank field and found nothing. The matcher must use the resolved name.
        var human = Human(burnerName: "", displayName: "Maria Garcia");

        var match = PersonSearchMatcher.Match(human, "maria", PersonSearchFields.Name);

        match.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("jose", "José")]
    [InlineData("munoz", "Muñoz")]
    [InlineData("JOSÉ", "josé")]
    public void Name_match_is_accent_and_case_insensitive(string query, string burnerName)
    {
        var human = Human(burnerName: burnerName);

        PersonSearchMatcher.Match(human, query, PersonSearchFields.Name).Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("sparkle pony")]
    [InlineData("pony sparkle")]
    [InlineData("PONY")]
    public void Name_match_splits_query_into_tokens(string query)
    {
        var human = Human(burnerName: "Sparkle Pony");

        PersonSearchMatcher.Match(human, query, PersonSearchFields.Name).Should().NotBeNull();
    }

    [HumansFact]
    public void Legal_name_matches_under_LegalName_scope_but_not_public()
    {
        // Burner "Sparkle" hides legal name "María García". Searching the real name must work in
        // admin/coordinator windows but NEVER in public search (would deanonymize the burner).
        var human = Human(burnerName: "Sparkle", firstName: "María", lastName: "García",
            displayName: "Sparkle");

        var match = PersonSearchMatcher.Match(human, "garcia maria", PersonSearchFields.ManageAll);
        match.Should().NotBeNull();
        match!.Field.Should().Be("Legal Name");
        PersonSearchMatcher.Match(human, "garcia maria", PersonSearchFields.PublicAll).Should().BeNull();
    }

    [HumansTheory]
    [InlineData("welding")]   // Bio
    [InlineData("berlin")]    // City
    [InlineData("kitchen")]   // ContributionInterests
    [InlineData("they")]      // Pronouns
    public void Bio_fields_match_under_Bio_scope(string query)
    {
        var human = Human(burnerName: "Sparkle", bio: "Loves welding", city: "Berlin",
            interests: "kitchen crew", pronouns: "they/them");

        PersonSearchMatcher.Match(human, query, PersonSearchFields.Bio).Should().NotBeNull();
    }

    [HumansFact]
    public void Volunteer_cv_matches_under_Bio_scope()
    {
        var human = Human(burnerName: "Sparkle", cv: [("Nowhere 2024", "Gate crew")]);

        PersonSearchMatcher.Match(human, "nowhere", PersonSearchFields.Bio).Should().NotBeNull();
    }

    [HumansFact]
    public void Public_contact_field_matches_under_Bio_but_private_one_does_not()
    {
        var pub = Human(burnerName: "Sparkle",
            contactFields: [("publichandle", ContactFieldVisibility.AllActiveProfiles)]);
        var priv = Human(burnerName: "Sparkle",
            contactFields: [("privatehandle", ContactFieldVisibility.BoardOnly)]);

        PersonSearchMatcher.Match(pub, "publichandle", PersonSearchFields.Bio).Should().NotBeNull();
        PersonSearchMatcher.Match(priv, "privatehandle", PersonSearchFields.Bio).Should().BeNull();
        PersonSearchMatcher.Match(priv, "privatehandle", PersonSearchFields.AdminAll).Should().NotBeNull();
    }

    [HumansFact]
    public void Publicly_exposed_email_matches_for_everyone_but_private_email_is_admin_only()
    {
        var pub = Human(burnerName: "Sparkle",
            emails: [("public@example.com", true, ContactFieldVisibility.AllActiveProfiles)]);
        var priv = Human(burnerName: "Sparkle",
            emails: [("login@example.com", true, ContactFieldVisibility.BoardOnly)]);

        PersonSearchMatcher.Match(pub, "public@example", PersonSearchFields.Bio).Should().NotBeNull();
        PersonSearchMatcher.Match(priv, "login@example", PersonSearchFields.Bio).Should().BeNull();
        PersonSearchMatcher.Match(priv, "login@example", PersonSearchFields.AdminAll).Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("adminnote")]
    [InlineData("boardnote")]
    [InlineData("consentnote")]
    [InlineData("rejectreason")]
    [InlineData("ES9121")]      // IBAN fragment
    [InlineData("penicillin")]  // medical
    [InlineData("janedoe")]     // emergency contact name
    public void Board_private_and_health_fields_never_match_even_under_AdminAll(string query)
    {
        var human = Human(
            burnerName: "Sparkle",
            adminNotes: "adminnote",
            boardNotes: "boardnote",
            consentCheckNotes: "consentnote",
            rejectionReason: "rejectreason",
            iban: "ES9121000418450200051332",
            medicalConditions: "penicillin allergy",
            emergencyContactName: "Jane Doe");

        PersonSearchMatcher.Match(human, query, PersonSearchFields.AdminAll).Should().BeNull();
    }

    [HumansFact]
    public void Rejected_profile_is_never_matched()
    {
        var human = Human(burnerName: "Sparkle", rejectedAt: At);

        PersonSearchMatcher.Match(human, "sparkle", PersonSearchFields.AdminAll).Should().BeNull();
    }

    [HumansTheory]
    [InlineData("jose", "José")]   // diacritic + case folded
    [InlineData("JOSÉ", "josé")]
    [InlineData("peter pan", "Peter Pan")]  // full multi-token string, exact
    public void ExactName_matches_folded_full_string_equality(string query, string burnerName)
    {
        var human = Human(burnerName: burnerName);

        var match = PersonSearchMatcher.Match(human, query, PersonSearchFields.ExactName);

        match.Should().NotBeNull();
        match!.Field.Should().Be("Exact Name");
    }

    [HumansTheory]
    [InlineData("Peter", "Peter Pan")]   // substring of name — must NOT match
    [InlineData("Pan", "Peter Pan")]     // token of name — must NOT match
    [InlineData("Joseph", "Jose")]       // prefix superset — must NOT match
    [InlineData("Jose", "Joseph")]       // prefix subset — must NOT match
    public void ExactName_is_not_substring_or_prefix(string query, string burnerName)
    {
        var human = Human(burnerName: burnerName);

        PersonSearchMatcher.Match(human, query, PersonSearchFields.ExactName).Should().BeNull();
    }

    [HumansFact]
    public void ExactName_never_matches_rejected_profile()
    {
        var human = Human(burnerName: "Sparkle", rejectedAt: At);

        PersonSearchMatcher.Match(human, "sparkle", PersonSearchFields.ExactName).Should().BeNull();
    }

    [HumansFact]
    public void ExactName_alone_does_not_match_bio_legal_or_admin_buckets()
    {
        // ExactName must be its own bit: it confines to the resolved display name only,
        // and never spills into Bio / Legal name / Admin email field buckets.
        var human = Human(
            burnerName: "Sparkle", firstName: "María", lastName: "García", displayName: "Sparkle",
            bio: "Loves welding", city: "Berlin", interests: "kitchen crew",
            emails: [("login@example.com", true, ContactFieldVisibility.BoardOnly)]);

        PersonSearchMatcher.Match(human, "welding", PersonSearchFields.ExactName).Should().BeNull();
        PersonSearchMatcher.Match(human, "berlin", PersonSearchFields.ExactName).Should().BeNull();
        PersonSearchMatcher.Match(human, "garcia maria", PersonSearchFields.ExactName).Should().BeNull();
        PersonSearchMatcher.Match(human, "login@example.com", PersonSearchFields.ExactName).Should().BeNull();
    }

    [HumansFact]
    public void ExactName_resolves_display_name_when_burnername_blank()
    {
        var human = Human(burnerName: "", displayName: "Maria Garcia");

        PersonSearchMatcher.Match(human, "maria garcia", PersonSearchFields.ExactName).Should().NotBeNull();
    }
}
