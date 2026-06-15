using AwesomeAssertions;
using Humans.Domain;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Users;

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

        UserStateClassifier.Classify(user, profile).Should().Be(UserState.Active);
    }

    [HumansFact]
    public void Classify_entity_returns_Bare_when_a_required_name_field_is_blank()
    {
        var user = NewUser(displayName: "Real Name");
        var profile = NewNamedProfile(user.Id);
        profile.LastName = "";

        UserStateClassifier.Classify(user, profile).Should().Be(UserState.Bare);
    }

    [HumansTheory]
    [InlineData(ProfileState.Suspended, UserState.Suspended)]
    [InlineData(ProfileState.AdminSuspended, UserState.AdminSuspended)]
    public void Classify_entity_preserves_suspension_reason(ProfileState profileState, UserState expected)
    {
        var user = NewUser(displayName: "Real Name");
        var profile = NewNamedProfile(user.Id);
        profile.State = profileState;

        UserStateClassifier.Classify(user, profile).Should().Be(expected);
    }

    [HumansFact]
    public void Classify_entity_distinguishes_merge_tombstone_from_gdpr_deletion()
    {
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);

        // Merge tombstone: MergedAt set, real DisplayName → Merged.
        var merged = NewUser(displayName: "Real Name");
        merged.MergedAt = instant;
        UserStateClassifier.Classify(merged, profile: null).Should().Be(UserState.Merged);

        // GDPR deletion reuses the merge tombstone columns, so MergedAt is also set — but the
        // "Deleted User" DisplayName sentinel must win and classify it as Deleted.
        var gdprDeleted = NewUser(displayName: UserStateClassifier.GdprAnonymizedDisplayName);
        gdprDeleted.MergedAt = instant;
        UserStateClassifier.Classify(gdprDeleted, profile: null).Should().Be(UserState.Deleted);
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
        State = ProfileState.Active,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };
}
