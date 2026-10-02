using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Containers.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Containers.Tests;

public class ContainerControllerTests
{
    [HumansFact]
    public async Task Create_ImageRuleFailure_FlashesTheLocalizedMessageNotTheEnglishException()
    {
        var actorId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "slug", "c@example.com", "+34600000000", false, 1, []);
        var camps = Substitute.For<ICampServiceRead>();
        camps.GetCampBySlugAsync("slug", Arg.Any<CancellationToken>()).Returns(camp);
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var containers = Substitute.For<IContainerService>();
        containers.CreateAsync(Arg.Any<ContainerData>(), actorId, Arg.Any<CancellationToken>())
            .Returns<ContainerDto>(_ => throw new ContainerRuleException(
                "Containers_Error_TooManyImages", "A container can have at most 5 images.", 5));
        var localizer = Substitute.For<IStringLocalizer<ContainersResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), "success"));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(call => new LocalizedString(
                call.ArgAt<string>(0), $"localized:{call.ArgAt<string>(0)}:{call.ArgAt<object[]>(1)[0]}"));

        var controller = new ContainerController(
            camps, containers, Substitute.For<ICityPlanningServiceRead>(), auth, users,
            localizer, NullLogger<ContainerController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], authenticationType: "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        var result = await controller.Create("slug", new ContainerFormModel { Name = "Box" }, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("localized:Containers_Error_TooManyImages:5");
    }

    private static UserInfo MakeUser(Guid id) => new(
        id, "actor", false, "en", null, SystemClock.Instance.GetCurrentInstant(), null, null,
        null, null, null, false, false, null, null, null, null, null, null, [], [], [], null, []);
}
