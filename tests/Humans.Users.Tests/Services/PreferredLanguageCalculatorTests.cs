using Humans.Users.Domain;
using AwesomeAssertions;
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
    public void Build_tallies_active_users_and_excludes_suspended()
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
            MakeUserInfo(u4Suspended, "fr", UserState.Suspended),
        };

        var result = PreferredLanguageCalculator.Build(snapshot);

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
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var component = new PreferredLanguageCardViewComponent(users)
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

    private static UserInfo MakeUserInfo(
        Guid id, string preferredLanguage, UserState state = UserState.Active) => UserInfoFactory.Create(
        user: new User
        {
            Id = id,
            DisplayName = "U",
            PreferredLanguage = preferredLanguage,
            State = state,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        },
        userEmails: [],
        externalLogins: [],
        profile: new Profile { UserId = id, BurnerName = "U", FirstName = "F", LastName = "L" },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: []);
}
