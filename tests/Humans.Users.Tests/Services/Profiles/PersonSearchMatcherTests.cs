using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using NodaTime;
using Xunit;
using Humans.Users.Services;

namespace Humans.Users.Tests.Services.Profiles;

public class PersonSearchMatcherTests
{
    [HumansTheory]
    [InlineData("en", "Name", "City", "Other", "Legal Name")]
    [InlineData("es", "Nombre", "Ciudad", "Otro", "Nombre legal")]
    [InlineData("de", "Name", "Stadt", "Sonstiges", "Bürgerlicher Name")]
    [InlineData("it", "Nome", "Città", "Altro", "Nome legale")]
    [InlineData("fr", "Nom", "Ville", "Autre", "Nom légal")]
    [InlineData("ca", "Nom", "Ciutat", "Altres", "Nom legal")]
    public void Built_in_match_labels_use_ui_culture_and_custom_labels_stay_verbatim(
        string culture, string name, string city, string other, string legalName)
    {
        using var scope = new CultureScope(culture);
        PersonSearchMatcher.Match(Human(burnerName: "Alice"), "Alice", PersonSearchFields.Name)!
            .Field.Should().Be(name);
        PersonSearchMatcher.Match(Human(firstName: "Alice"), "Alice", PersonSearchFields.LegalName)!
            .Field.Should().Be(legalName);
        PersonSearchMatcher.Match(Human(city: "Barcelona"), "Barcelona", PersonSearchFields.Bio)!
            .Field.Should().Be(city);
        PersonSearchMatcher.Match(Human(contactFields: [("12345", ContactFieldVisibility.AllActiveProfiles)]),
            "12345", PersonSearchFields.Bio)!.Field.Should().Be(other);
        PersonSearchMatcher.Match(Human(contactLabel: "City", contactFields: [("12345", ContactFieldVisibility.AllActiveProfiles)]),
            "12345", PersonSearchFields.Bio)!.Field.Should().Be("City");
    }

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
        string? contactLabel = null,
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
            // Every real Profile save copies BurnerName onto the owning User row.
            BurnerName = burnerName,
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = At,
        };

        var contactEntities = (contactFields ?? [])
            .Select((c, i) => new ContactField
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                FieldType = ContactFieldType.Other,
                Value = c.Value,
                CustomLabel = contactLabel,
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

        return UserInfoFactory.Create(
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
    public void Matches_nothing_when_burnername_blank_even_with_a_legacy_DisplayName()
    {
        // No DisplayName fallback for an ordinary (non-sentinel) row: a blank BurnerName renders
        // and searches blank, even with a legacy DisplayName set.
        var human = Human(burnerName: "", displayName: "Maria Garcia");

        var match = PersonSearchMatcher.Match(human, "maria", PersonSearchFields.Name);

        match.Should().BeNull();
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

    [HumansTheory]
    [InlineData("joel", "Joël")]   // unaccented query finds accented name
    [InlineData("Joël", "joel")]   // accented query finds unaccented name
    public void Name_match_finds_accented_and_unaccented_joel(string query, string burnerName)
    {
        var human = Human(burnerName: burnerName);

        PersonSearchMatcher.Match(human, query, PersonSearchFields.Name).Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("Ian", 100)]         // exact
    [InlineData("Ian Smith", 85)]    // whole-string prefix
    [InlineData("Bob Ian", 80)]      // token prefix
    [InlineData("Brian", 60)]        // substring only
    public void Name_match_score_reflects_match_quality(string burnerName, int expectedScore)
    {
        // This ranking is what floats the literal "Ian" above "Adrian"/"Brian" instead of
        // alphabetizing them. Controllers sort by Score; the matcher assigns it.
        var human = Human(burnerName: burnerName);

        var match = PersonSearchMatcher.Match(human, "ian", PersonSearchFields.Name);

        match.Should().NotBeNull();
        match.Score.Should().Be(expectedScore);
    }

    [HumansFact]
    public void Name_match_outscores_non_name_field_match()
    {
        // Someone named "Ian" must rank above someone whose bio merely mentions "Ian".
        var named = Human(burnerName: "Ian");
        var bioOnly = Human(burnerName: "Zara", bio: "I volunteered with Ian last year");

        var namedMatch = PersonSearchMatcher.Match(named, "ian", PersonSearchFields.PublicAll);
        var bioMatch = PersonSearchMatcher.Match(bioOnly, "ian", PersonSearchFields.PublicAll);

        namedMatch.Should().NotBeNull();
        bioMatch.Should().NotBeNull();
        namedMatch.Score.Should().BeGreaterThan(bioMatch.Score);
    }

    [HumansFact]
    public void Legal_name_matches_under_AdminAll_scope_but_not_public()
    {
        // Burner "Sparkle" hides legal name "María García". Searching the real name must work in
        // admin/coordinator windows but NEVER in public search (would deanonymize the burner).
        var human = Human(burnerName: "Sparkle", firstName: "María", lastName: "García",
            displayName: "Sparkle");

        var match = PersonSearchMatcher.Match(human, "garcia maria", PersonSearchFields.AdminAll);
        match.Should().NotBeNull();
        match.Field.Should().Be("Legal Name");
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

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LongFormMatch_SnippetEdgesPreserveUnicode(bool interests, bool atStart)
    {
        var text = atStart
            ? new string('x', 39) + "😀" + new string('x', 59) + "needle" + "tail"
            : "needle" + new string('x', 59) + "😀" + "tail";
        var human = interests ? Human(interests: text) : Human(bio: text);

        var match = PersonSearchMatcher.Match(human, "needle", PersonSearchFields.Bio);

        match.Should().NotBeNull();
        match.Snippet.Should().Contain("needle").And.Contain("😀");
        Action encode = () => new System.Text.UTF8Encoding(false, true).GetBytes(match.Snippet!);
        encode.Should().NotThrow();
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

    [HumansFact]
    public void IsSearchable_is_profile_present_and_not_rejected()
    {
        // The single-person lookups (/api/profiles/by-userid, allowEmail) read this same flag.
        Human().IsSearchable.Should().BeTrue();
        Human(rejectedAt: At).IsSearchable.Should().BeFalse();
        UserInfo.Create(new User { Id = Guid.NewGuid(), PreferredLanguage = "en" }, [], [], [], profile: null, [])
            .IsSearchable.Should().BeFalse();
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
        match.Field.Should().Be("Exact Name");
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
    public void ExactName_does_not_resolve_display_name_when_burnername_blank()
    {
        // No DisplayName fallback: a blank BurnerName searches blank.
        var human = Human(burnerName: "", displayName: "Maria Garcia");

        PersonSearchMatcher.Match(human, "maria garcia", PersonSearchFields.ExactName).Should().BeNull();
    }
}
