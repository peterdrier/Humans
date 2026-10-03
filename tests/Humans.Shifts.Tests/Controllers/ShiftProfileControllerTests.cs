using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Shifts.Controllers;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Controllers;

public class ShiftProfileControllerTests
{
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task ShiftInfo_RejectsOversizedFreeTextWithoutChangingTheProfile(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var shared = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var shifts = Substitute.For<IShiftManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        var userId = Guid.NewGuid();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, [])));
        var profile = new VolunteerEventProfile { Id = Guid.NewGuid(), UserId = userId };
        shifts.GetOrCreateShiftProfileAsync(userId).Returns(profile);
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
        };
        var controller = new ShiftProfileController(shifts, users, shared,
            services.GetRequiredService<IStringLocalizer<ShiftsResource>>(), NullLogger<ShiftProfileController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>(),
        };
        var model = new ShiftInfoViewModel
        {
            SelectedSkills = ["Other"], SkillOtherText = new string('s', 200),
            SelectedLanguages = ["Other"], LanguageOtherText = new string('l', 200),
        };
        validator.Validate(controller.ControllerContext, null, "", model);
        controller.ModelState.IsValid.Should().BeTrue();
        (await controller.ShiftInfo(model)).Should().BeOfType<RedirectToActionResult>();
        profile.Skills.Should().ContainSingle().Which.Should().Be("Other: " + new string('s', 200));
        profile.Languages.Should().ContainSingle().Which.Should().Be("Other: " + new string('l', 200));
        shifts.ClearReceivedCalls();
        controller.ModelState.Clear();

        model.SkillOtherText += "s";
        model.LanguageOtherText += "l";
        validator.Validate(controller.ControllerContext, null, "", model);
        (await controller.ShiftInfo(model)).Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        foreach (var field in new[] { nameof(model.SkillOtherText), nameof(model.LanguageOtherText) })
            controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be(shared["Validation_MaxLength", field, 200].Value);
        await shifts.DidNotReceive().GetOrCreateShiftProfileAsync(Arg.Any<Guid>());
        await shifts.DidNotReceive().UpdateShiftProfileAsync(Arg.Any<VolunteerEventProfile>());
        profile.Skills.Should().ContainSingle().Which.Should().HaveLength(207);
        profile.Languages.Should().ContainSingle().Which.Should().HaveLength(207);
    }
}
