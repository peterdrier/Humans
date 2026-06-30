using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models.EarlyEntry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class EarlyEntryRosterControllerTests
{
    private readonly IEarlyEntryService _earlyEntryService = Substitute.For<IEarlyEntryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();

    private EarlyEntryRosterController BuildSut()
    {
        var ctrl = new EarlyEntryRosterController(_earlyEntryService, _userService);

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var urlHelperFactory = Substitute.For<IUrlHelperFactory>();
        urlHelperFactory.GetUrlHelper(Arg.Any<ActionContext>())
            .Returns(Substitute.For<IUrlHelper>());
        services.AddSingleton(urlHelperFactory);
        http.RequestServices = services.BuildServiceProvider();

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = "Index",
            },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    [HumansFact]
    public async Task Index_SingleMultiSourceRow_ReturnsCorrectViewModel()
    {
        var userId = Guid.NewGuid();
        var entryDate = new LocalDate(2026, 8, 25);

        _earlyEntryService.GetRosterAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EarlyEntryRosterRow>
            {
                new(userId, entryDate, ["Camp Alpha", "Build Shift"], HasMultiple: true),
            });

        var ctrl = BuildSut();
        var result = await ctrl.Index(Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<EarlyEntryRosterViewModel>().Subject;
        model.Rows.Should().HaveCount(1);
        model.Rows[0].UserId.Should().Be(userId);
        model.Rows[0].HasMultiple.Should().BeTrue();
        model.Rows[0].Sources.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Index_PopulatesLegalNameFromProfile()
    {
        var userId = Guid.NewGuid();

        _earlyEntryService.GetRosterAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EarlyEntryRosterRow>
            {
                new(userId, new LocalDate(2026, 8, 25), ["Camp Alpha"], HasMultiple: false),
            });
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId, burner: "Spark", first: "Jean", last: "Dupont"));

        var ctrl = BuildSut();
        var result = await ctrl.Index(Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<EarlyEntryRosterViewModel>().Subject;
        model.Rows[0].LegalName.Should().Be("Jean Dupont");
    }

    private static UserInfo MakeUserInfo(Guid userId, string burner, string first, string last) =>
        UserInfo.Create(
            user: new User { Id = userId, DisplayName = burner, PreferredLanguage = "en", CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0) },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                UserId = userId,
                BurnerName = burner,
                FirstName = first,
                LastName = last,
                State = ProfileState.Active,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
