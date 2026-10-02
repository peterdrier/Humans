using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Containers.Tests;

public sealed class ContainerValidationLocalizationTests
{
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task MemberValidationErrors_AreLocalized(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<ContainersResource>>();
        var userId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var containers = Substitute.For<IContainerService>();
        containers.GetByIdAsync(containerId, Arg.Any<CancellationToken>()).Returns(
            new ContainerDto(containerId, Guid.NewGuid(), "Container", null, [], Instant.MinValue, Instant.MinValue));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new ContainerController(Substitute.For<ICampServiceRead>(), containers,
            Substitute.For<ICityPlanningServiceRead>(), authorization, users, localizer, NullLogger<ContainerController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        foreach (var key in new[]
        {
            "Containers_Error_InvalidName", "Containers_Error_TooManyImages", "Containers_Error_ImageType",
            "Containers_Error_ImageSize", "Containers_Error_ImageFileNameLength", "Containers_Error_ImageExtension"
        })
        {
            containers.UpdateAsync(containerId, Arg.Any<ContainerData>(), userId, Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException(key));

            await controller.Edit("camp", containerId, new ContainerFormModel { Name = "Container" }, TestContext.Current.CancellationToken);

            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }

        var validator = services.GetRequiredService<IObjectModelValidator>();
        var shared = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        foreach (var (model, field, key, arguments) in new (ContainerFormModel, string, string, object[])[]
        {
            (new() { Name = "" }, "Name", "Validation_Required", ["Name"]),
            (new() { Name = "<container>" }, "Name", "Validation_InvalidCharacters", ["Name", "[^<>$]*"]),
            (new() { Name = new string('x', 257) }, "Name", "Validation_MaxLength", ["Name", 256]),
            (new() { Name = "Container", Description = new string('x', 2001) }, "Description", "Validation_MaxLength", ["Description", 2000])
        })
        {
            var context = new ActionContext { HttpContext = http };
            validator.Validate(context, null, "", model);
            var expected = shared[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }
}
