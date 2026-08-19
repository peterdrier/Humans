using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Services;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services.Dashboard;

/// <summary>
/// Unit tests for the admin-dashboard aggregator extracted from
/// <c>OnboardingService</c> in nobodies-collective#584. Verifies that the
/// aggregation produces the same shape as the previous OnboardingService
/// implementation.
/// </summary>
public class AdminDashboardServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IMembershipCalculatorRead _membershipCalculator = Substitute.For<IMembershipCalculatorRead>();
    private readonly IApplicationServiceRead _applicationDecisionService = Substitute.For<IApplicationServiceRead>();
    private readonly IBurnSettingsService _shiftManagement = Substitute.For<IBurnSettingsService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly ITicketServiceRead _ticketQueryService = Substitute.For<ITicketServiceRead>();

    public AdminDashboardServiceTests()
    {
        // Default stubs: no active event, empty shift view. Per-test overrides allowed.
        _shiftManagement.GetActiveAsync().Returns((BurnSettingsInfo?)null);
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = (IEnumerable<Guid>)ci[0];
                IReadOnlyDictionary<Guid, ShiftUserSummary> dict =
                    ids.ToDictionary(id => id, id => ShiftUserSummary.Empty(id));
                return new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(dict);
            });
    }

    private AdminDashboardService BuildSut() =>
        new(_userService, _membershipCalculator, _applicationDecisionService,
            _shiftManagement, _shiftView, _ticketQueryService);

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
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(snapshot));

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

        var result = await sut.GetAdminDashboardAsync(Xunit.TestContext.Current.CancellationToken);

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
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: null,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
