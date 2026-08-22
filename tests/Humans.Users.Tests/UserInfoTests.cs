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
    public void Create_carries_communication_preferences_projection()
    {
        var userId = Guid.NewGuid();
        var prefs = new[]
        {
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
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
                Category = MessageCategory.Governance,
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
        info.CommunicationPreferences.Should().HaveCount(2);
        info.CommunicationPreferences.Select(c => c.Category)
            .Should().Equal(MessageCategory.Marketing, MessageCategory.Governance);
        info.CommunicationPreferences[0].OptedOut.Should().BeFalse();
        info.CommunicationPreferences[0].UpdateSource.Should().Be("Profile");
    }

    [HumansFact]
    public void GoogleEmailStatus_falls_back_to_verified_provider_row_when_no_IsGoogle_row()
    {
        // ~pre-#687 users have no IsGoogle row; sync targets the verified provider (OAuth) row,
        // so its rejection must drive suppression (Codex review on #1015).
        var userId = Guid.NewGuid();
        var providerRow = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "fallback@example.com",
            IsVerified = true,
            IsGoogle = false,
            Provider = "Google",
            ProviderKey = "sub-1",
            GoogleEmailStatus = GoogleEmailStatus.Rejected,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };

        var info = UserInfoFactory.Create(
            MinimalUser(userId), [providerRow], [], [], profile: null, [], [], [], []);

        info.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Rejected);
    }

    [HumansFact]
    public void GoogleEmailStatus_prefers_IsGoogle_row_over_provider_fallback()
    {
        var userId = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var isGoogleRow = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "canonical@example.com",
            IsVerified = true,
            IsGoogle = true,
            Provider = "Google",
            ProviderKey = "sub-canon",
            GoogleEmailStatus = GoogleEmailStatus.Valid,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var providerFallback = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "fallback@example.com",
            IsVerified = true,
            IsGoogle = false,
            Provider = "Google",
            ProviderKey = "sub-fb",
            GoogleEmailStatus = GoogleEmailStatus.Rejected,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var info = UserInfoFactory.Create(
            MinimalUser(userId), [isGoogleRow, providerFallback], [], [], profile: null, [], [], [], []);

        info.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Valid);
    }

    [HumansFact]
    public void MarketingOptedOut_is_null_when_no_marketing_pref()
    {
        var info = UserInfoFactory.Create(
            MinimalUser(),
            [],
            [],
            [],
            profile: null,
            [],
            [],
            [],
            []);

        info.MarketingOptedOut.Should().BeNull();
    }

    [HumansFact]
    public void MarketingOptedOut_reflects_pref_when_present()
    {
        var userId = Guid.NewGuid();
        var prefs = new[]
        {
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = true,
                InboxEnabled = true,
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
                UpdateSource = "Profile",
            },
        };

        var info = UserInfoFactory.Create(
            MinimalUser(userId),
            [],
            [],
            [],
            profile: null,
            [],
            [],
            [],
            prefs);

        info.MarketingOptedOut.Should().BeTrue();
    }

    [HumansFact]
    public void HasTicket_true_when_any_participation_is_Ticketed_or_Attended()
    {
        var participations = new[]
        {
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Year = 2026,
                Status = ParticipationStatus.Ticketed,
                Source = ParticipationSource.TicketSync,
            }
        };

        var info = UserInfoFactory.Create(
            MinimalUser(),
            [],
            participations,
            [],
            profile: null,
            [],
            [],
            [],
            []);

        info.HasTicket.Should().BeTrue();
    }

    [HumansFact]
    public void HasTicketForYear_only_matches_the_requested_year()
    {
        var participations = new[]
        {
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Year = 2025,
                Status = ParticipationStatus.Attended,
                Source = ParticipationSource.TicketSync,
            }
        };

        var info = UserInfoFactory.Create(
            MinimalUser(),
            [],
            participations,
            [],
            profile: null,
            [],
            [],
            [],
            []);

        info.HasTicketForYear(2025).Should().BeTrue();
        info.HasTicketForYear(2026).Should().BeFalse();
        // Year-agnostic accessor still sees the prior-year ticket.
        info.HasTicket.Should().BeTrue();
    }

    [HumansFact]
    public void HasTicket_false_when_only_NotAttending_or_no_participations()
    {
        var participations = new[]
        {
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Year = 2026,
                Status = ParticipationStatus.NotAttending,
                Source = ParticipationSource.UserDeclared,
            }
        };

        var info = UserInfoFactory.Create(
            MinimalUser(),
            [],
            participations,
            [],
            profile: null,
            [],
            [],
            [],
            []);

        info.HasTicket.Should().BeFalse();
    }

    // #1097 — resolution order User.BurnerName → Profile.BurnerName → legacy DisplayName.

    [HumansFact]
    public void BurnerName_prefers_the_User_column_over_the_Profile()
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
    public void BurnerName_falls_back_to_the_Profile_when_the_User_column_is_blank(string? userBurnerName)
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = userBurnerName;

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, "From Profile"), [], [], [], []);

        info.BurnerName.Should().Be("From Profile");
    }

    [HumansFact]
    public void BurnerName_falls_back_to_DisplayName_when_both_names_are_blank()
    {
        var userId = Guid.NewGuid();
        var user = MinimalUser(userId);
        user.BurnerName = null;

        var info = UserInfoFactory.Create(
            user, [], [], [], NamedProfile(userId, ""), [], [], [], []);

        info.BurnerName.Should().Be("Test");
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
