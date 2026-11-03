using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using NodaTime;

namespace Humans.Users.Tests.Services;

public class PreferredLanguageCalculatorTests
{
    [HumansFact]
    public void Build_tallies_Active_and_MissingConsents_but_excludes_Suspended()
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
            // Suspended user must NOT be counted — verifies the filter respects the
            // Active + MissingConsents union.
            MakeUserInfo(u4Suspended, "fr"),
        };
        var partition = new MembershipPartition(
            IncompleteSignup: [],
            PendingApproval: [],
            Active: [u1, u2],
            MissingConsents: [u3],
            Suspended: [u4Suspended],
            PendingDeletion: []);

        var result = PreferredLanguageCalculator.Build(snapshot, partition);

        result.Should().HaveCount(2);
        result.Should().Contain(l => l.Language == "en" && l.Count == 2);
        result.Should().Contain(l => l.Language == "es" && l.Count == 1);
        result.Should().NotContain(l => l.Language == "fr");
    }

    [HumansFact]
    public async Task Card_orders_by_count_descending()
    {
        var enUsers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var esUser = Guid.NewGuid();
        var snapshot = new[] { MakeUserInfo(esUser, "es") }
            .Concat(enUsers.Select(id => MakeUserInfo(id, "en")))
            .ToList();
        var partition = new MembershipPartition(
            IncompleteSignup: [],
            PendingApproval: [],
            Active: [.. enUsers, esUser],
            MissingConsents: [],
            Suspended: [],
            PendingDeletion: []);

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var membership = Substitute.For<IMembershipCalculator>();
        membership.PartitionUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(partition);
        var component = new PreferredLanguageCardViewComponent(users, membership)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() }
            }
        };

        var view = (await component.InvokeAsync()).Should().BeOfType<ViewViewComponentResult>().Subject;
        var result = view.ViewData!.Model.Should().BeAssignableTo<IReadOnlyList<PreferredLanguageCount>>().Subject;
        result.Select(l => l.Language).Should().Equal("en", "es");
    }

    private static UserInfo MakeUserInfo(Guid id, string preferredLanguage) => UserInfoFactory.Create(
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
