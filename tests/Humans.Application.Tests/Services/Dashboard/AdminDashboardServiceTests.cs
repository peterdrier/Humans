using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Dashboard;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Dashboard;

/// <summary>
/// Unit tests for the admin-dashboard aggregator extracted from
/// <c>OnboardingService</c> in nobodies-collective#584. Verifies that the
/// aggregation produces the same shape as the previous OnboardingService
/// implementation.
/// </summary>
public class AdminDashboardServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IApplicationDecisionService _applicationDecisionService = Substitute.For<IApplicationDecisionService>();

    private AdminDashboardService BuildSut() =>
        new(_userService, _profileService, _membershipCalculator, _applicationDecisionService);

    [HumansFact]
    public async Task GetAdminDashboardAsync_AggregatesPartitionAppStatsAndLanguageDistribution()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        var u4Suspended = Guid.NewGuid();
        var snapshot = new List<UserInfo>
        {
            MakeUserInfo(u1, "en"),
            MakeUserInfo(u2, "en"),
            MakeUserInfo(u3, "es"),
            // Suspended user must NOT be counted in the language tally — verifies
            // the in-memory filter respects the Active+MissingConsents union.
            MakeUserInfo(u4Suspended, "fr"),
        };
        _userService.GetAllUserInfos().Returns(snapshot);

        var partition = new MembershipPartition(
            IncompleteSignup: [],
            PendingApproval: [],
            Active: [u1, u2],
            MissingConsents: [u3],
            Suspended: [u4Suspended],
            PendingDeletion: []);
        _membershipCalculator.PartitionUsersAsync(
                Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == 4),
                Arg.Any<CancellationToken>())
            .Returns(partition);

        _applicationDecisionService.GetPendingApplicationCountAsync(Arg.Any<CancellationToken>())
            .Returns(7);
        _applicationDecisionService.GetAdminStatsAsync(Arg.Any<CancellationToken>())
            .Returns(new ApplicationAdminStats(
                Total: 50, Approved: 40, Rejected: 10,
                ColaboradorApplied: 30, AsociadoApplied: 20));

        var sut = BuildSut();

        var result = await sut.GetAdminDashboardAsync();

        result.TotalMembers.Should().Be(4);
        result.ActiveMembers.Should().Be(2);
        result.MissingConsents.Should().Be(1);
        result.Suspended.Should().Be(1);
        result.PendingApplications.Should().Be(7);
        result.TotalApplications.Should().Be(50);
        result.ApprovedApplications.Should().Be(40);
        result.RejectedApplications.Should().Be(10);
        result.ColaboradorApplied.Should().Be(30);
        result.AsociadoApplied.Should().Be(20);
        result.LanguageDistribution.Should().HaveCount(2);
        result.LanguageDistribution.Should().Contain(l => l.Language == "en" && l.Count == 2);
        result.LanguageDistribution.Should().Contain(l => l.Language == "es" && l.Count == 1);
        // Suspended user's "fr" must NOT appear.
        result.LanguageDistribution.Should().NotContain(l => l.Language == "fr");
    }

    private static UserInfo MakeUserInfo(Guid id, string preferredLanguage) => UserInfo.Create(
        user: new User
        {
            Id = id,
            DisplayName = "U",
            PreferredLanguage = preferredLanguage,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: Array.Empty<UserEmail>(),
        eventParticipations: Array.Empty<EventParticipation>(),
        externalLogins: Array.Empty<(string, string)>(),
        profile: null,
        contactFields: Array.Empty<ContactField>(),
        profileLanguages: Array.Empty<ProfileLanguage>(),
        volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
        communicationPreferences: Array.Empty<CommunicationPreference>());

    [HumansFact]
    public async Task GetPendingReviewCountAsync_DelegatesToProfileService()
    {
        _profileService.GetPendingReviewCountAsync(Arg.Any<CancellationToken>()).Returns(42);

        var sut = BuildSut();

        var result = await sut.GetPendingReviewCountAsync();

        result.Should().Be(42);
        await _profileService.Received(1).GetPendingReviewCountAsync(Arg.Any<CancellationToken>());
    }
}
