using AwesomeAssertions;
using NodaTime;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Domain;

namespace Humans.Users.Tests.Services.Users;

/// <summary>
/// Verifies <see cref="UserStateClassifier"/> — the single precedence authority for
/// <see cref="UserState"/>. Precedence (most-final wins):
/// Merged &gt; Deleted &gt; Rejected &gt; Suspended &gt; DeletePending &gt; Bare &gt; Active.
/// </summary>
public class UserStateClassifierTests
{
    [HumansTheory]
    // hasName, suspended, adminSuspended, rejected, deletionPending, merged, gdprDeleted, expected
    [InlineData(true, false, false, false, false, false, false, UserState.Active)]
    [InlineData(false, false, false, false, false, false, false, UserState.Bare)]
    [InlineData(false, false, false, false, true, false, false, UserState.DeletePending)] // DeletePending > Bare
    [InlineData(true, true, false, false, true, false, false, UserState.Suspended)] // Suspended > DeletePending
    [InlineData(true, false, true, false, true, false, false, UserState.AdminSuspended)] // AdminSuspended > DeletePending
    [InlineData(true, true, true, false, true, false, false, UserState.AdminSuspended)] // AdminSuspended > Suspended
    [InlineData(true, true, true, true, true, false, false, UserState.Rejected)] // Rejected > Suspended
    [InlineData(true, true, true, true, true, false, true, UserState.Deleted)] // Deleted > Rejected
    [InlineData(true, true, true, true, true, true, false, UserState.Merged)] // Merged > all below
    [InlineData(true, true, true, true, true, true, true, UserState.Merged)] // Merged is the top of the ladder
    public void Classify_applies_precedence(
        bool hasName,
        bool suspended,
        bool adminSuspended,
        bool rejected,
        bool deletionPending,
        bool merged,
        bool gdprDeleted,
        UserState expected)
    {
        UserStateClassifier.Classify(
                hasRequiredNameFields: hasName,
                isSuspended: suspended,
                isAdminSuspended: adminSuspended,
                isRejected: rejected,
                isDeletionPending: deletionPending,
                isMerged: merged,
                isGdprDeleted: gdprDeleted)
            .Should().Be(expected);
    }

    [HumansFact]
    public void Classify_entity_returns_Active_for_a_named_unflagged_profile()
    {
        var user = NewUser(displayName: "Real Name");
        var profile = NewNamedProfile(user.Id);

        UserStateEvaluator.Classify(user, profile).Should().Be(UserState.Active);
    }

    [HumansFact]
    public void Classify_entity_returns_Bare_when_a_required_name_field_is_blank()
    {
        var user = NewUser(displayName: "Real Name");
        var profile = NewNamedProfile(user.Id);
        profile.LastName = "";

        UserStateEvaluator.Classify(user, profile).Should().Be(UserState.Bare);
    }

    [HumansTheory]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    public void Classify_entity_carries_stored_suspension_forward(UserState stored)
    {
        var user = NewUser(displayName: "Real Name");
        user.State = stored;
        var profile = NewNamedProfile(user.Id);

        UserStateEvaluator.Classify(user, profile).Should().Be(stored);
    }

    [HumansTheory]
    [InlineData(false, false, UserState.Active)]
    [InlineData(true, false, UserState.Suspended)]
    [InlineData(true, true, UserState.AdminSuspended)]
    public void Classify_entity_takes_the_suspension_the_transition_supplies(
        bool isSuspended, bool isAdminSuspended, UserState expected)
    {
        var user = NewUser(displayName: "Real Name");
        user.State = UserState.AdminSuspended;
        var profile = NewNamedProfile(user.Id);

        UserStateEvaluator.Classify(user, profile, isSuspended, isAdminSuspended)
            .Should().Be(expected);
    }

    [HumansFact]
    public void Classify_entity_distinguishes_merge_tombstone_from_gdpr_deletion()
    {
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);

        // Merge tombstone: MergedAt set, real DisplayName → Merged.
        var merged = NewUser(displayName: "Real Name");
        merged.MergedAt = instant;
        UserStateEvaluator.Classify(merged, profile: null).Should().Be(UserState.Merged);

        // GDPR deletion reuses the merge tombstone columns, so MergedAt is also set — but the
        // minted tombstone email must win and classify it as Deleted.
        var gdprDeleted = NewUser(displayName: "Real Name");
        gdprDeleted.MergedAt = instant;
        gdprDeleted.Email = $"deleted-{gdprDeleted.Id:N}@deleted.local";
        UserStateEvaluator.Classify(gdprDeleted, profile: null).Should().Be(UserState.Deleted);
    }

    // Regression test for nobodies-collective/Humans#1742: a member typing "Deleted User" as their
    // own burner name must not be classified as GDPR-deleted and lose application access. Fails
    // against the pre-fix BurnerName arm in UserStateEvaluator.IsGdprTombstoned.
    [HumansFact]
    public void Classify_entity_does_not_treat_a_user_typed_BurnerName_as_deletion()
    {
        var user = NewUser(displayName: "Real Name");
        user.BurnerName = UserStateClassifier.GdprAnonymizedDisplayName;
        user.Email = "burner@example.com";
        var profile = NewNamedProfile(user.Id);

        UserStateEvaluator.Classify(user, profile).Should().Be(UserState.Active);
    }

    [HumansFact]
    public void Classify_entity_recognises_the_minted_tombstone_email()
    {
        var user = NewUser(displayName: "Real Name");
        user.Email = $"deleted-{user.Id:N}@deleted.local";

        UserStateEvaluator.Classify(user, profile: null).Should().Be(UserState.Deleted);
    }

    [HumansFact]
    public void Classify_entity_recognises_the_legacy_tombstone_shape_without_the_scrubbed_email()
    {
        // Rows anonymized before the email scrub was added to the erasure path keep a normal-looking
        // email but carry the complete legacy tombstone shape across all three name columns.
        var user = NewUser(displayName: UserStateClassifier.GdprAnonymizedDisplayName);
        user.FirstName = "Deleted";
        user.LastName = "User";
        user.Email = "legacy@example.com";

        UserStateEvaluator.Classify(user, profile: null).Should().Be(UserState.Deleted);
    }

    [HumansFact]
    public void Classify_entity_does_not_treat_the_DisplayName_sentinel_alone_as_deletion()
    {
        // Narrower than pre-#1742: the legacy fallback requires the complete tombstone shape, not
        // the DisplayName sentinel on its own — a normal FirstName/LastName/email must stay Active.
        var user = NewUser(displayName: UserStateClassifier.GdprAnonymizedDisplayName);
        user.FirstName = "First";
        user.LastName = "Last";
        user.Email = "normal@example.com";
        var profile = NewNamedProfile(user.Id);

        UserStateEvaluator.Classify(user, profile).Should().Be(UserState.Active);
    }

    private static User NewUser(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static Profile NewNamedProfile(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BurnerName = "Burner",
        FirstName = "First",
        LastName = "Last",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };
}
