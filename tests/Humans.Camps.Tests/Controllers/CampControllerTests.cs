using System.Security.Claims;
using System.Reflection;
using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Enums;
using Humans.Base.Constants;
using Humans.Base;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Camps.Tests.Controllers;

public class CampControllerTests
{
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ICampContactService _contacts = Substitute.For<ICampContactService>();
    private readonly ICampRoleService _roles = Substitute.For<ICampRoleService>();
    private readonly ICityPlanningService _cityPlanning = Substitute.For<ICityPlanningService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<CampsResource> _campsLocalizer = Substitute.For<IStringLocalizer<CampsResource>>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_stops_loading_when_the_request_is_abandoned(bool cancelAfterSettings)
    {
        using var request = new CancellationTokenSource();
        var abandon = false;
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            if (abandon && cancelAfterSettings) await request.CancelAsync();
            return new CampSettingsInfo(2026, [2026]);
        });
        _cityPlanning.GetRegistrationInfoAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return "Registration instructions";
        });
        var controller = BuildController(Guid.NewGuid());
        controller.HttpContext.RequestAborted = request.Token;
        (await controller.Register()).Should().BeOfType<ViewResult>();
        controller.ViewData["SeasonYear"].Should().Be(2026);
        controller.ViewData["RegistrationInfo"].Should().Be("Registration instructions");

        abandon = true;
        if (!cancelAfterSettings) await request.CancelAsync();
        Func<Task> load = async () => await controller.Register();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public void UploadImage_allows_one_valid_image_without_accepting_a_larger_request()
    {
        var limit = typeof(CampController).GetMethod(nameof(CampController.UploadImage))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(11L * 1024 * 1024);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task CampForms_ValidateExistingFieldLimitsBeforeWriting(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var shared = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        foreach (var edit in new[] { false, true })
        {
            var model = edit ? new CampEditViewModel() : new CampRegisterViewModel();
            // MVC converts an empty posted email field to null before validation.
            model.ContactEmail = null!;
            var controller = BuildController(userId);
            controller.HttpContext.RequestServices = services;
            validator.Validate(controller.ControllerContext, null, "", model);
            foreach (var field in new[] { "Name", "ContactEmail", "BlurbLong", "BlurbShort", "Languages" })
            {
                controller.ModelState.Should().ContainKey(field);
                controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage
                    .Should().Be(shared["Validation_Required"].Value);
            }

            controller.ModelState.Clear();
            model.Name = new string('n', 257);
            model.ContactEmail = new string('a', 245) + "@example.com";
            model.ContactPhone = "+" + new string('1', 64);
            model.BlurbLong = new string('x', 4001);
            model.BlurbShort = new string('x', 1001);
            model.Languages = new string('x', 257);
            model.KidsAreaDescription = new string('x', 2001);
            model.PerformanceTypes = new string('x', 1001);
            validator.Validate(controller.ControllerContext, null, "", model);
            foreach (var (field, max) in new[]
            {
                ("Name", 256), ("ContactEmail", 256), ("ContactPhone", 64), ("BlurbLong", 4000),
                ("BlurbShort", 1000), ("Languages", 256), ("KidsAreaDescription", 2000), ("PerformanceTypes", 1000)
            })
            {
                controller.ModelState.Should().ContainKey(field);
                controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage
                    .Should().Be(shared["Validation_MaxLength", field, max].Value);
            }

            var result = edit ? await controller.Edit(camp.Slug, (CampEditViewModel)model) : await controller.Register(model);
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);

            controller.ModelState.Clear();
            model.Name = new string('n', 256);
            model.ContactEmail = new string('a', 244) + "@example.com";
            model.ContactPhone = "+" + new string('1', 63);
            model.BlurbLong = new string('x', 4000);
            model.BlurbShort = new string('x', 1000);
            model.Languages = new string('x', 256);
            model.KidsAreaDescription = new string('x', 2000);
            model.PerformanceTypes = new string('x', 1000);
            validator.Validate(controller.ControllerContext, null, "", model);
            controller.ModelState.IsValid.Should().BeTrue();

            controller.ModelState.Clear();
            model.ContactEmail = "invalid email";
            validator.Validate(controller.ControllerContext, null, "", model);
            controller.ModelState.Should().ContainKey("ContactEmail");
            controller.ModelState["ContactEmail"]!.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be(shared["Validation_EmailAddress"].Value);
        }
        _camps.ReceivedCalls().Should().NotContain(call =>
            call.GetMethodInfo().Name == "CreateCampAsync" || call.GetMethodInfo().Name == "UpdateCampAsync");
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task RegistrationAndRenewal_LocalizeRuleFailures(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        var reservedKey = "Camps_Flash_ReservedName";
        _camps.CreateCampAsync(default, default!, default!, default!, null, null, false, 0,
                default!, null, 0, default)
            .ReturnsForAnyArgs(Task.FromException<Camp>(new InvalidOperationException(reservedKey)));
        var controller = BuildController(userId, localizer);
        var model = new CampRegisterViewModel { Name = "Register" };

        (await controller.Register(model)).Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        var expected = localizer[reservedKey, model.Name];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);

        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        foreach (var key in new[]
        {
            "Camps_Flash_SeasonNotOpen", "Camps_Flash_SeasonAlreadyExists", "Camps_Flash_NoPreviousSeason"
        })
        {
            _camps.OptInToSeasonAsync(camp.Id, 2027, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<CampSeason>(new InvalidOperationException(key)));
            (await controller.OptIn(camp.Slug, 2027)).Should().BeOfType<RedirectToActionResult>();
            expected = localizer[key, 2027];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task Register_LocalizesThePhoneFieldInValidationErrors(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var campsLocalizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var sharedLocalizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2026]));
        var controller = BuildController(Guid.NewGuid(), campsLocalizer, sharedLocalizer);
        var model = new CampRegisterViewModel { ContactPhone = "612 345 678" };

        var result = await controller.Register(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        var label = campsLocalizer["Camp_ContactPhoneLabel"];
        label.ResourceNotFound.Should().BeFalse();
        var expected = sharedLocalizer["Validation_PhoneE164", label.Value];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[nameof(model.ContactPhone)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(expected.Value);
        _camps.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "CreateCampAsync");
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task UploadImage_LocalizesValidationFailures(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var controller = BuildController(userId, localizer);
        using var content = new MemoryStream([1]);
        var file = new FormFile(content, 0, 1, "file", "camp.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
        foreach (var key in new[]
        {
            "Camps_Validation_ImageCount", "Camps_Validation_ImageType", "Camps_Validation_ImageSize",
            "Camps_Validation_ImageFilenameLength", "Camps_Validation_ImageExtension"
        })
        {
            _camps.UploadImageAsync(camp.Id, Arg.Any<Stream>(), "camp.jpg", "image/jpeg", 1, Arg.Any<CancellationToken>())
                .Returns(CampImageUploadResult.Failure(key));

            await controller.UploadImage(camp.Slug, file);

            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task SelfMembershipActions_LocalizeRuleFailures(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId, localizer);

        foreach (var key in new[] { "Camps_Flash_RoleMemberNotFound", "Camps_Flash_LeaveRequiresActive" })
        {
            _camps.LeaveCampAsync(memberId, userId, Arg.Any<CancellationToken>())
                .Returns(CampMembershipMutationResult.Failure(key));
            await controller.LeaveMembership(camp.Slug, memberId);
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
        foreach (var key in new[] { "Camps_Flash_RoleMemberNotFound", "Camps_Flash_WithdrawRequiresPending" })
        {
            _camps.WithdrawCampMembershipRequestAsync(memberId, userId, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException(key)));
            await controller.WithdrawMembershipRequest(camp.Slug, memberId);
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task LeadActions_LocalizeGuardFailures(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: actorId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var logger = Substitute.For<ILogger<CampController>>();
        var controller = BuildController(actorId, localizer, logger: logger);
        foreach (var (action, key) in new[]
        {
            ("Approve", "Camps_Flash_ApproveRequiresPending"),
            ("Reject", "Camps_Flash_RejectRequiresPending"),
            ("Remove", "Camps_Flash_RemoveRequiresActive"),
            ("Approve", "Camps_Flash_RoleMemberNotFound"),
            ("Reject", "Camps_Flash_RoleMemberNotFound"),
            ("Remove", "Camps_Flash_RoleMemberNotFound"),
            ("RemoveHistoricalName", "Camps_Flash_CampNotFound"),
            ("RemoveHistoricalName", "Camps_Flash_HistoricalNameNotFound"),
            ("RemoveHistoricalName", "Camps_Flash_HistoricalNameWrongCamp"),
            ("DeleteImage", "Camps_Flash_ImageNotFound"),
            ("DeleteImage", "Camps_Flash_ImageWrongCamp"),
        })
        {
            logger.ClearReceivedCalls();
            var failure = new InvalidOperationException(key);
            Task<IActionResult> result;
            switch (action)
            {
                case "Approve":
                    _camps.ApproveCampMemberAsync(camp.Id, targetId, actorId).ThrowsAsync(failure);
                    result = controller.ApproveMembership(camp.Slug, targetId); break;
                case "Reject":
                    _camps.RejectCampMemberAsync(camp.Id, targetId, actorId).ThrowsAsync(failure);
                    result = controller.RejectMembership(camp.Slug, targetId); break;
                case "Remove":
                    _camps.RemoveCampMemberAsync(camp.Id, targetId, actorId).ThrowsAsync(failure);
                    result = controller.RemoveMembership(camp.Slug, targetId); break;
                case "RemoveHistoricalName":
                    _camps.RemoveHistoricalNameAsync(camp.Id, targetId).ThrowsAsync(failure);
                    result = controller.RemoveHistoricalName(camp.Slug, targetId); break;
                case "DeleteImage":
                    _camps.DeleteImageAsync(camp.Id, targetId).ThrowsAsync(failure);
                    result = controller.DeleteImage(camp.Slug, targetId); break;
                default: throw new InvalidOperationException("Unknown test action");
            }
            (await result).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(action is "RemoveHistoricalName" or "DeleteImage" ? "Edit" : "Members");
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
            var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
            args[0].Should().Be(LogLevel.Warning);
            args[3].Should().BeNull();
            args[2]!.ToString().Should().Contain(targetId.ToString()).And.Contain(camp.Id.ToString()).And.Contain(key);
        }
    }

    [HumansTheory]
    [InlineData("Approve", false)]
    [InlineData("Reject", false)]
    [InlineData("Remove", false)]
    [InlineData("Withdraw", false)]
    [InlineData("Approve", true)]
    [InlineData("Reject", true)]
    [InlineData("Remove", true)]
    [InlineData("Withdraw", true)]
    public async Task MembershipAction_LogsExpectedRejectionAndPropagatesUnexpectedFailure(string action, bool unexpected)
    {
        var actorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: actorId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        const string reason = "Camps_Flash_RoleMemberNotFound";
        _campsLocalizer[reason].Returns(new LocalizedString(reason, "Member not found"));
        Exception failure = unexpected ? new IOException("Storage unavailable") : new InvalidOperationException(reason);
        switch (action)
        {
            case "Approve": _camps.ApproveCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); break;
            case "Reject": _camps.RejectCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); break;
            case "Remove": _camps.RemoveCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); break;
            case "Withdraw": _camps.WithdrawCampMembershipRequestAsync(memberId, actorId).ThrowsAsync(failure); break;
        }
        var logger = Substitute.For<ILogger<CampController>>();
        var controller = BuildController(actorId, logger: logger);
        Func<Task<IActionResult>> act = action switch
        {
            "Approve" => () => controller.ApproveMembership(camp.Slug, memberId),
            "Reject" => () => controller.RejectMembership(camp.Slug, memberId),
            "Remove" => () => controller.RemoveMembership(camp.Slug, memberId),
            "Withdraw" => () => controller.WithdrawMembershipRequest(camp.Slug, memberId),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (unexpected)
        {
            await act.Should().ThrowAsync<IOException>().WithMessage("Storage unavailable");
            logger.ReceivedCalls().Should().BeEmpty();
            return;
        }
        var result = (await act()).Should().BeOfType<RedirectToActionResult>().Subject;
        result.ActionName.Should().Be(action is "Withdraw" ? "Details" : "Members");
        result.RouteValues!["slug"].Should().Be(camp.Slug);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Member not found");
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(memberId.ToString()).And.Contain(reason);
        args[2]!.ToString().Should().Contain((action is "Withdraw" ? actorId : camp.Id).ToString());
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void ContactForm_LocalizesRequiredAndLengthMessages(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        foreach (var (message, key, arguments) in new (string, string, object[])[]
        {
            ("", "Validation_Required", ["Message"]),
            (new string('x', 2001), "Validation_MaxLength", ["Message", 2000])
        })
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", new CampContactViewModel { Message = message });
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState["Message"]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task Index_BuildsPublicDirectory_FromCachedCampInfoRead()
    {
        var matching = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, kidsWelcome: YesNoMaybe.Yes);
        var filteredOut = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Full, kidsWelcome: YesNoMaybe.No);
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([matching, filteredOut, pending]);
        var controller = BuildController();

        var result = await controller.Index(new CampFilterViewModel { KidsFriendly = true }, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Year.Should().Be(2026);
        vm.Camps.Should().ContainSingle(c => c.Id == matching.Id);
        vm.MyCamps.Should().BeEmpty();
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
        await _camps.Received(1).GetSettingsAsync(Arg.Any<CancellationToken>());
        await _camps.Received(1).GetCampsForYearAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_InvalidFilterQueryValue_ClearsModelStateAndRendersWithDefaults()
    {
        // Regression for nobodies-collective/Humans#926: a scanner payload in a bool
        // filter param leaves an invalid attempted value in ModelState, which the
        // checkbox tag helper would throw a FormatException re-converting at render.
        StubCampReadModel([]);
        var controller = BuildController();
        const string garbage = "test\")))EXTRACTVALUE(7966,...)";
        controller.ModelState.SetModelValue(
            "ShowLeadPositions", new Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult(garbage));
        controller.ModelState.AddModelError(
            "ShowLeadPositions", $"The value '{garbage}' is not valid for ShowLeadPositions.");

        var result = await controller.Index(new CampFilterViewModel(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        controller.ModelState.Should().BeEmpty(
            "the invalid attempted value must not survive to view rendering");
    }

    [HumansFact]
    public async Task Index_RoleLeadPendingCamp_AppearsInMyCamps_FromCampInfo()
    {
        var userId = Guid.NewGuid();
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending, leadUserId: userId);
        StubCampReadModel([pending]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Should().BeEmpty();
        vm.MyCamps.Should().ContainSingle(c => c.Id == pending.Id);
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
    }

    [HumansTheory]
    [InlineData(nameof(CampMemberRequestOutcome.Created), nameof(CampMemberRequestNoticeLevel.Success), "Camps_Flash_RequestCreated", TempDataKeys.SuccessMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.AlreadyActive), nameof(CampMemberRequestNoticeLevel.Info), "Camps_Flash_RequestAlreadyActive", TempDataKeys.InfoMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.AlreadyPending), nameof(CampMemberRequestNoticeLevel.Info), "Camps_Flash_RequestAlreadyPending", TempDataKeys.InfoMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.NoOpenSeason), nameof(CampMemberRequestNoticeLevel.Error), "Camps_Flash_RequestNoOpenSeason", TempDataKeys.ErrorMessage)]
    public async Task RequestMembership_RendersLocalizedOutcome(
        string outcome, string noticeLevel, string messageKey, string tempDataKey)
    {
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _camps.RequestCampMembershipAsync(camp.Id, userId, Arg.Any<CancellationToken>())
            .Returns(new CampMemberRequestResult(Guid.NewGuid(), Enum.Parse<CampMemberRequestOutcome>(outcome), messageKey, Enum.Parse<CampMemberRequestNoticeLevel>(noticeLevel)));
        const string translated = "Solicitud de incorporación traducida";
        _campsLocalizer[messageKey].Returns(new LocalizedString(messageKey, translated));
        var controller = BuildController(userId);

        var result = await controller.RequestMembership(camp.Slug);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(CampController.Details));
        redirect.RouteValues!["slug"].Should().Be(camp.Slug);
        controller.TempData[tempDataKey].Should().Be(translated);
    }

    [HumansFact]
    public async Task AddMember_PropagatesRequestCancellation()
    {
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _camps.AddCampMemberToActiveSeasonAsync(camp.Id, Arg.Any<Guid>(), actorId, cancellation.Token)
            .Returns(Task.FromCanceled<AddCampMemberOutcome>(cancellation.Token));

        var act = () => BuildController(actorId).AddMember(camp.Slug, Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Index_RoleLeadPublicCamp_IsPinnedBeforeAlphabeticalCamps()
    {
        var userId = Guid.NewGuid();
        var alphabeticalFirst = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        var leadCamp = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Active, leadUserId: userId);
        StubCampReadModel([alphabeticalFirst, leadCamp]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Select(c => c.Id).Should().Equal(leadCamp.Id, alphabeticalFirst.Id);
    }

    [HumansFact]
    public async Task Members_Shows_Active_Event_Shift_Counts_From_Shift_View()
    {
        var leadUserId = Guid.NewGuid();
        var zeroShiftMemberId = Guid.NewGuid();
        var oneShiftMemberId = Guid.NewGuid();
        var fivePlusShiftMemberId = Guid.NewGuid();

        var members = new[]
        {
            MakeSeasonMember(zeroShiftMemberId),
            MakeSeasonMember(oneShiftMemberId),
            MakeSeasonMember(fivePlusShiftMemberId),
        };
        var camp = MakeCamp("garden-of-joy", "Garden of Joy", CampSeasonStatus.Active, leadUserId, members: members);
        var season = camp.Seasons.Single();

        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _camps.GetCampEditDataAsync(camp.Id, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampEditData?>(MakeEditData(camp, season)));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                call.ArgAt<IReadOnlyCollection<Guid>>(0).ToDictionary(id => id, MakeUserInfo)));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _roles.BuildPanelAsync(season.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CampRolesPanelData(season.Id, [])));
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(
                new Dictionary<Guid, ShiftUserSummary>
                {
                    [zeroShiftMemberId] = MakeShiftUserSummary(zeroShiftMemberId),
                    [oneShiftMemberId] = MakeShiftUserSummary(oneShiftMemberId, SignupStatus.Confirmed),
                    [fivePlusShiftMemberId] = MakeShiftUserSummary(
                        fivePlusShiftMemberId,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Bailed),
                }));

        var controller = BuildController(leadUserId);

        var result = await controller.Members(camp.Slug, null, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampEditViewModel>().Subject;
        vm.ActiveMembers.Single(m => m.UserId == zeroShiftMemberId).EventShiftSignupCount.Should().Be(0);
        vm.ActiveMembers.Single(m => m.UserId == oneShiftMemberId).EventShiftSignupCount.Should().Be(1);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupCount.Should().Be(5);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupDisplay.Should().Be("5+");
    }

    [HumansFact]
    public async Task Details_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // Destination-page half of the nobodies-collective/Humans#985 ruling: global search
        // stops offering a camp once its public-year season leaves Active/Full, and the id
        // path deliberately does not — so /Camps/{slug} has to be the enforcement point
        // (nobodies-collective/Humans#993).
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(pending.Slug, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SeasonDetails_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // Equivalent nobodies-collective/Humans#993 coverage for the arbitrary-year route.
        var withdrawn = MakeCamp("withdrawn-camp", "Withdrawn Camp", CampSeasonStatus.Withdrawn);
        StubCampReadModel([withdrawn]);
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().SeasonDetails(withdrawn.Slug, 2026, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Details_NonPublicSeason_Lead_StillRenders()
    {
        // The #993 gate must not lock a lead out of their own Pending camp.
        var leadUserId = Guid.NewGuid();
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending, leadUserId: leadUserId);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, false, null, null, null, null, null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId).Details(pending.Slug, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Details_FutureSeasonOnlyCamp_Lead_StillRenders()
    {
        // A camp registered for an open future year has no PublicYear season, so the
        // public-year projection cannot answer "is this user its lead" — the #993 gate
        // must authorize against the loaded camp, or it 404s the lead's own camp right
        // after registration.
        var leadUserId = Guid.NewGuid();
        var future = MakeCamp("future-camp", "Future Camp", CampSeasonStatus.Pending,
            leadUserId: leadUserId, year: 2027);
        StubCampReadModel([]); // the PublicYear (2026) projection does not contain the camp
        _camps.GetCampBySlugAsync(future.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(future));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, false, null, null, null, null, null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        // Succeed only for the loaded CampInfo resource — mirrors the real handler, which
        // sees the lead assignment on the camp itself but not through the id lookup.
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => ReferenceEquals(resource, future)),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId).Details(future.Slug, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>(
            because: "the lead of a not-yet-public camp must reach their own camp page");
    }

    [HumansFact]
    public async Task SeasonDetails_OptedInRenewalSeason_PriorSeasonLead_StillRenders()
    {
        // OptInToSeasonAsync copies no lead assignments into the renewal season, so the
        // year projection for the new year carries no leads — viewer state must resolve
        // from the slug-loaded camp, whose earlier seasons carry them.
        var leadUserId = Guid.NewGuid();
        var renewal = MakeCamp("garden", "Garden Camp", CampSeasonStatus.Pending, year: 2027);
        var priorSeason = renewal.Seasons.Single() with
        {
            Id = Guid.NewGuid(),
            Year = 2026,
            Status = CampSeasonStatus.Active,
            LeadUserIds = [leadUserId]
        };
        var fullCamp = renewal with { Seasons = [priorSeason, renewal.Seasons.Single()] };
        StubCampReadModel([]);
        _camps.GetCampsForYearAsync(2027, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampInfo>>([renewal]));
        _camps.GetCampBySlugAsync(renewal.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(fullCamp));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, false, null, null, null, null, null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        // Succeed only for the slug-loaded camp — the year projection cannot answer the
        // lead check, exactly as the real handler behaves.
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => ReferenceEquals(resource, fullCamp)),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId)
            .SeasonDetails(renewal.Slug, 2027, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>(
            because: "a lead must reach their camp's not-yet-approved renewal season");
    }

    [HumansFact]
    public async Task Details_AnonymousRender_CarriesNoMemberOrEarlyEntryData()
    {
        // Behavioral pin for "membership/EE data never render publicly": walk the actual
        // anonymous view-model graph — a leak that is renamed or nested still shows up
        // as the member's id or an Ee*/EarlyEntry-named node somewhere in the graph.
        var memberUserId = Guid.NewGuid();
        var member = new CampSeasonMemberInfo(
            Guid.NewGuid(), memberUserId, CampMemberStatus.Active,
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0),
            HasEarlyEntry: true);
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, members: [member]);
        StubCampReadModel([camp]);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(camp.Slug, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject.Model!;
        var leaves = new List<(string Path, object? Value)>();
        CollectLeaves(vm, "vm", leaves, []);

        leaves.Should().NotContain(
            l => Equals(l.Value, memberUserId) || memberUserId.ToString().Equals(l.Value as string, StringComparison.OrdinalIgnoreCase),
            because: "an anonymous detail render must not carry any member identity");
        leaves.Where(l => l.Path.Split('.', '[')
                .Any(seg => seg.StartsWith("Ee", StringComparison.Ordinal)
                            || seg.Contains("EarlyEntry", StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty("Early Entry state is admin-only (issue #490, spec §4.4) and must not reach the public detail shape");
    }

    private static void CollectLeaves(object? obj, string path, List<(string Path, object? Value)> leaves, HashSet<object> seen)
    {
        if (obj is null)
        {
            return;
        }

        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum || obj is string || obj is Guid || obj is decimal
            || type.Namespace?.StartsWith("NodaTime", StringComparison.Ordinal) == true)
        {
            leaves.Add((path, obj));
            return;
        }

        if (obj is System.Collections.IEnumerable enumerable)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                CollectLeaves(item, $"{path}[{i++}]", leaves, seen);
            }

            return;
        }

        if (!seen.Add(obj))
        {
            return;
        }

        foreach (var property in type.GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            CollectLeaves(property.GetValue(obj), $"{path}.{property.Name}", leaves, seen);
        }
    }

    private void StubCampReadModel(IReadOnlyList<CampInfo> camps)
    {
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CampSettingsInfo(2026, [2026])));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(camps));
    }

    private CampController BuildController(Guid? userId = null, IStringLocalizer<CampsResource>? campsLocalizer = null,
        IStringLocalizer<SharedResource>? sharedLocalizer = null, ILogger<CampController>? logger = null)
    {
        var controller = new CampController(
            _camps,
            _contacts,
            _roles,
            _cityPlanning,
            _shiftView,
            _users,
            _authorization,
            _clock,
            logger ?? NullLogger<CampController>.Instance,
            campsLocalizer ?? _campsLocalizer,
            sharedLocalizer ?? _sharedLocalizer);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())],
                authenticationType: "test"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampController.Index) }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    private static CampInfo MakeCamp(
        string slug,
        string name,
        CampSeasonStatus status,
        Guid? leadUserId = null,
        YesNoMaybe kidsWelcome = YesNoMaybe.Yes,
        IReadOnlyList<CampSeasonMemberInfo>? members = null,
        int year = 2026)
    {
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(),
            campId,
            slug,
            year,
            NameLockDate: null,
            name,
            $"{name} short",
            Languages: "en",
            Vibes: [CampVibe.ChillOut],
            status,
            AcceptingMembers: YesNoMaybe.Yes,
            kidsWelcome,
            AdultPlayspacePolicy.No,
            MemberCount: 0,
            SoundZone: SoundZone.Green,
            SpaceRequirement: null,
            ElectricalGrid: null,
            EeSlotCount: 0,
            EeGrantedCount: 0,
            JoinedMemberCount: 0)
        {
            LeadUserIds = leadUserId.HasValue ? [leadUserId.Value] : [],
            Members = members ?? []
        };

        return new CampInfo(
            campId,
            slug,
            ContactEmail: $"{slug}@example.com",
            ContactPhone: "+34600000000",
            IsSwissCamp: false,
            TimesAtNowhere: 1,
            Seasons: [season]);
    }

    private static CampSeasonMemberInfo MakeSeasonMember(Guid userId) =>
        new(
            Guid.NewGuid(),
            userId,
            CampMemberStatus.Active,
            SystemClock.Instance.GetCurrentInstant(),
            SystemClock.Instance.GetCurrentInstant(),
            HasEarlyEntry: false);

    private static CampEditData MakeEditData(CampInfo camp, CampSeasonInfo season) =>
        new(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
            IsNameLocked: false,
            season.Name,
            camp.ContactEmail,
            camp.ContactPhone,
            Links: [],
            camp.IsSwissCamp,
            camp.HideHistoricalNames,
            camp.TimesAtNowhere,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes,
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ElectricalGrid,
            Images: [],
            HistoricalNames: []);

    private static ShiftUserSummary MakeShiftUserSummary(Guid userId, params SignupStatus[] statuses) =>
        ShiftFixtures.UserSummary(
            userId,
            [.. statuses.Select(status => ShiftFixtures.Signup(status: status))]);

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            UserFixtures.Profile(
                burnerName: "Lead Human",
                isApproved: true),
            []);
}
