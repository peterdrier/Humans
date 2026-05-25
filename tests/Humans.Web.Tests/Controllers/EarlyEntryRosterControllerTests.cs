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
using Xunit;

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

    private static UserInfo StubUserInfo(Guid userId, string burnerName)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = burnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = burnerName,
            FirstName = burnerName,
            LastName = "Test",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
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

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(StubUserInfo(userId, "Spanner")));

        var ctrl = BuildSut();
        var result = await ctrl.Index(CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<EarlyEntryRosterViewModel>().Subject;
        model.Rows.Should().HaveCount(1);
        model.Rows[0].DisplayName.Should().Be("Spanner");
        model.Rows[0].HasMultiple.Should().BeTrue();
        model.Rows[0].Sources.Should().HaveCount(2);
    }
}
