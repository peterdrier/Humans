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

    [HumansFact]
    public void MarketingOptedOut_is_null_when_no_marketing_pref()
    {
        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            Array.Empty<EventParticipation>(),
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

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

        var info = UserInfo.Create(
            MinimalUser(userId),
            Array.Empty<UserEmail>(),
            Array.Empty<EventParticipation>(),
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
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

        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            participations,
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

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

        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            participations,
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

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

        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            participations,
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

        info.HasTicket.Should().BeFalse();
    }
}
