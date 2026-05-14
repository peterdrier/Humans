using AwesomeAssertions;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests;

public class UserInfoTests
{
    private static User MinimalUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = "Test",
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        GoogleEmailStatus = GoogleEmailStatus.Unknown,
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

        var info = UserInfo.Create(
            user: MinimalUser(userId),
            userEmails: Array.Empty<UserEmail>(),
            eventParticipations: Array.Empty<EventParticipation>(),
            externalLogins: Array.Empty<(string, string)>(),
            profile: null,
            contactFields: Array.Empty<ContactField>(),
            profileLanguages: Array.Empty<ProfileLanguage>(),
            volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
            communicationPreferences: prefs);

        // Marketing (3) sorts before Governance (4) by enum value ascending.
        info.CommunicationPreferences.Should().HaveCount(2);
        info.CommunicationPreferences.Select(c => c.Category)
            .Should().Equal(MessageCategory.Marketing, MessageCategory.Governance);
        info.CommunicationPreferences[0].OptedOut.Should().BeFalse();
        info.CommunicationPreferences[0].UpdateSource.Should().Be("Profile");
    }
}
