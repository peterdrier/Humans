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

    [HumansTheory]
    [InlineData(UserState.Active, true, true, false)]
    [InlineData(UserState.Bare, true, true, true)]
    [InlineData(UserState.Suspended, true, false, false)]
    [InlineData(UserState.AdminSuspended, true, false, false)]
    [InlineData(UserState.Rejected, true, true, false)]
    [InlineData(UserState.Active, false, false, true)]
    public void ProfileReadPredicates_States_FollowBrowsingRules(
        UserState state, bool hasProfile, bool browsable, bool needsSetup)
    {
        var user = MinimalUser();
        user.State = state;
        var info = UserInfoFactory.Create(user, [], [], hasProfile ? NamedProfile(user.Id) : null, [], [], []);

        info.IsProfileBrowsable.Should().Be(browsable);
        info.NeedsProfileSetup.Should().Be(needsSetup);
    }

    // User.BurnerName is the sole source: no Profile.BurnerName or legacy-DisplayName fallback.

    [HumansFact]
    public void BurnerName_Set_ReadsUserColumn()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = "From User";

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.BurnerName.Should().Be("From User");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BurnerName_UserColumnBlank_SkipsProfile(string? userBurnerName)
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = userBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.BurnerName.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("Old name")]
    [InlineData(UserInfo.GdprAnonymizedBurnerName)]
    public void BurnerName_Blank_SkipsDisplayName(string legacyName)
    {
        // Neither an ordinary name nor the old erasure sentinel supplies BurnerName.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.DisplayName = legacyName;
        user.BurnerName = null;

        var info = UserInfoFactory.Create(
            user, [], [], profile: null, [], [], []);

        info.BurnerName.Should().BeEmpty();
    }

    [HumansFact]
    public void BurnerName_ErasedUser_ReturnsSentinel()
    {
        // Mirrors UserRepository.ApplyExpiredDeletionAnonymizationAsync, which writes the
        // sentinel into BurnerName directly.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = UserInfo.GdprAnonymizedBurnerName;
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], profile: null, [], [], []);

        info.BurnerName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }

    [HumansFact]
    public void IsActive_MergedTombstone_IsFalse()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.MergedAt = Instant.FromUtc(2026, 1, 1, 0, 0);

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_GdprTombstone_IsFalse()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;
        // The minted tombstone email ApplyExpiredDeletionAnonymizationAsync writes — the
        // real post-erasure shape, not the user-editable DisplayName sentinel alone.
        user.Email = $"deleted-{userId:N}@deleted.local";

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_LegacyLocalEmailTombstone_IsFalse()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.Email = "someone@merged.local";

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsActive.Should().BeFalse();
    }

    // IsGdprAnonymized keys on the minted deleted-<id>@deleted.local email, never on user-editable names.

    [HumansFact]
    public void IsGdprAnonymized_MemberNamedDeletedUser_IsFalse()
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
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsGdprAnonymized.Should().BeFalse();
        info.IsTombstone.Should().BeFalse();
    }

    [HumansFact]
    public void IsGdprAnonymized_ErasedUser_IsTrue()
    {
        // The minted tombstone email ApplyExpiredDeletionAnonymizationAsync writes.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.Email = $"deleted-{userId:N}@deleted.local";

        var info = UserInfoFactory.Create(
            user, [], [], profile: null, [], [], []);

        info.IsGdprAnonymized.Should().BeTrue();
        info.IsTombstone.Should().BeTrue();
        info.BurnerName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }

    [HumansFact]
    public void IsGdprAnonymized_NameOnlyTombstone_IsFalse()
    {
        // Names alone never identify erased accounts.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;
        user.FirstName = "Deleted";
        user.LastName = "User";
        user.BurnerName = string.Empty;
        user.Email = "legacy-erased@example.com"; // ordinary email — pre-scrub shape

        var info = UserInfoFactory.Create(
            user, [], [], profile: null, [], [], []);

        info.IsGdprAnonymized.Should().BeFalse();
        info.IsTombstone.Should().BeFalse();
    }

    [HumansFact]
    public void IsGdprAnonymized_TypedTombstoneShape_IsFalse()
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
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsGdprAnonymized.Should().BeFalse();
        info.IsTombstone.Should().BeFalse();
    }

    [HumansFact]
    public void IsTombstone_MergedWithoutMergedAt_IsTrue()
    {
        var user = MinimalUser();
        user.State = UserState.Merged;

        var info = UserInfoFactory.Create(user, [], [], profile: null, [], [], []);

        info.IsTombstone.Should().BeTrue();
    }

    [HumansFact]
    public void IsTombstone_SentinelBehindVerifiedRow_IsTrue()
    {
        // Email resolves to the verified row, so only the raw column shows the merge sentinel.
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.Email = $"{userId:N}@merged.local";
        var row = new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "real@x.com", IsVerified = true, IsPrimary = true };

        var info = UserInfoFactory.Create(user, [row], [], profile: null, [], [], []);

        info.IsTombstone.Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_OrdinaryProfiledRow_IsTrue()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_RejectedRow_IsFalse()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.State = UserState.Rejected;

        var info = UserInfoFactory.Create(
            user, [], [], NamedProfile(userId), [], [], []);

        info.IsActive.Should().BeFalse();
    }

    private static Profile NamedProfile(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };
}
