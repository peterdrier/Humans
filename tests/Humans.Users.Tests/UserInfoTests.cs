using AwesomeAssertions;
using Humans.Users.Services;
using NodaTime;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Users.Tests;

public class UserInfoTests
{
    private static User MinimalUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = "Test",
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    [HumansFact]
    public void Create_carries_legacy_communication_preferences_projection()
    {
        var userId = Guid.NewGuid();
        var prefs = new[]
        {
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = LegacyMessageCategory.Marketing,
                OptedOut = false,
                InboxEnabled = true,
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
                UpdateSource = "Profile",
                SubscribedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
            },
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = LegacyMessageCategory.Governance,
                OptedOut = true,
                InboxEnabled = false,
                UpdatedAt = Instant.FromUtc(2026, 4, 2, 0, 0),
                UpdateSource = "MagicLink",
                SubscribedAt = null,
            },
        };

        var info = UserInfoFactory.Create(
            user: MinimalUser(userId),
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: prefs);

        // Marketing (3) sorts before Governance (4) by enum value ascending.
#pragma warning disable HUM_USER_COMMPREFS // pins the frozen legacy slice Email's fallback and copy read
        info.CommunicationPreferences.Should().HaveCount(2);
        info.CommunicationPreferences.Select(c => c.Category)
            .Should().Equal(LegacyMessageCategory.Marketing, LegacyMessageCategory.Governance);
        info.CommunicationPreferences[0].OptedOut.Should().BeFalse();
        info.CommunicationPreferences[0].UpdateSource.Should().Be("Profile");
#pragma warning restore HUM_USER_COMMPREFS
    }

    // User.BurnerName is the sole source; the one exception is recognising the GDPR-erasure
    // sentinel on legacy rows (see the tombstone tests below).

    [HumansFact]
    public void BurnerName_reads_only_the_User_column()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = "From User";

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "From Profile"), [], [], [], []);

        info.BurnerName.Should().Be("From User");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BurnerName_does_not_fall_back_to_the_Profile_when_the_User_column_is_blank(string? userBurnerName)
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = userBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "From Profile"), [], [], [], []);

        info.BurnerName.Should().BeEmpty();
    }

    [HumansFact]
    public void BurnerName_does_not_fall_back_to_a_non_sentinel_DisplayName()
    {
        // A blank BurnerName with an ordinary (non-sentinel) legacy DisplayName must render
        // blank, not the stale legacy name.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId); // DisplayName = "Test", not the GDPR sentinel.
        user.BurnerName = null;

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.BurnerName.Should().BeEmpty();
    }

    [HumansFact]
    public void BurnerName_resolves_the_sentinel_for_a_freshly_erased_user()
    {
        // Mirrors UserRepository.ApplyExpiredDeletionAnonymizationAsync, which writes the
        // sentinel into BurnerName directly.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = UserInfo.GdprAnonymizedBurnerName;
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.BurnerName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }

    [HumansFact]
    public void BurnerName_resolves_the_sentinel_for_a_legacy_erased_row()
    {
        // Legacy shape: erasure only nulled BurnerName and left the sentinel
        // in DisplayName. Narrow tombstone recognition must still surface it, not blank.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = null;
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.BurnerName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }

    // IsActive excludes tombstones: merged, GDPR-anonymized and legacy @merged.local/@deleted.local
    // rows all keep a Profile row but must not read active.

    [HumansFact]
    public void IsActive_false_for_merged_tombstone()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.MergedAt = Instant.FromUtc(2026, 1, 1, 0, 0);

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Merged"), [], [], [], []);

        info.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_false_for_gdpr_anonymized_tombstone()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;
        // The minted tombstone email ApplyExpiredDeletionAnonymizationAsync writes — the
        // real post-erasure shape, not the user-editable DisplayName sentinel alone.
        user.Email = $"deleted-{userId:N}@deleted.local";

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Deleted User"), [], [], [], []);

        info.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_false_for_legacy_shaped_gdpr_anonymized_tombstone()
    {
        // Legacy shape: BurnerName null, sentinel only in DisplayName.
        // Must still classify as a tombstone (and resolve BurnerName to the sentinel).
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = null;
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.IsActive.Should().BeFalse();
        info.BurnerName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }

    [HumansFact]
    public void IsActive_false_for_legacy_local_email_tombstone()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.Email = "someone@merged.local";

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Merged"), [], [], [], []);

        info.IsActive.Should().BeFalse();
    }

    // IsGdprAnonymized keys on the minted deleted-<id>@deleted.local email or, legacy-only, the
    // complete DisplayName+FirstName+LastName tombstone, never a user-editable name field alone.

    [HumansFact]
    public void IsGdprAnonymized_false_for_a_live_member_named_Deleted_User()
    {
        // A burner name a member typed themselves must never read as erased.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = "Deleted User";
        user.DisplayName = "Deleted User";
        user.FirstName = "Alice";
        user.LastName = "Smith";
        user.Email = "alice@example.com";

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Deleted User"), [], [], [], []);

        info.IsGdprAnonymized.Should().BeFalse();
        info.IsTombstone.Should().BeFalse();
    }

    [HumansFact]
    public void IsGdprAnonymized_true_for_a_genuinely_erased_user()
    {
        // The minted tombstone email ApplyExpiredDeletionAnonymizationAsync writes.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.Email = $"deleted-{userId:N}@deleted.local";

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.IsGdprAnonymized.Should().BeTrue();
        info.IsTombstone.Should().BeTrue();
    }

    [HumansFact]
    public void IsGdprAnonymized_true_for_the_legacy_name_only_tombstone()
    {
        // Rows anonymized before the email scrub joined the erasure path lack the tombstoned
        // email, so the complete legacy name tombstone (all three columns) must still count.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;
        user.FirstName = "Deleted";
        user.LastName = "User";
        // Legacy erasure cleared BurnerName without writing the sentinel there, so blank is the
        // pre-scrub shape and the part a member cannot reproduce. Set explicitly: it is load-bearing.
        user.BurnerName = string.Empty;
        user.Email = "legacy-erased@example.com"; // ordinary email — pre-scrub shape

        var info = UserInfoFactory.Create(
            user, [], [], [], profile: null, [], [], [], []);

        info.IsGdprAnonymized.Should().BeTrue();
    }

    [HumansFact]
    public void IsGdprAnonymized_false_for_a_live_member_who_types_the_whole_tombstone_shape()
    {
        // DisplayName, FirstName and LastName are all copied
        // from what a member types, so the legacy arm must not fire on names alone. Their
        // burner name is populated; a genuinely erased row's is blank.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = "Deleted User";
        user.DisplayName = "Deleted User";
        user.FirstName = "Deleted";
        user.LastName = "User";
        user.Email = "real.member@example.com";

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Deleted User"), [], [], [], []);

        info.IsGdprAnonymized.Should().BeFalse();
        info.IsTombstone.Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_true_for_ordinary_profiled_non_rejected_row()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Test"), [], [], [], []);

        info.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_false_for_rejected_row()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.State = UserState.Rejected;

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "Test"), [], [], [], []);

        info.IsActive.Should().BeFalse();
    }

    private static Profile NamedProfile(Guid userId, string burnerName) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BurnerName = burnerName,
        FirstName = "First",
        LastName = "Last",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };
}
