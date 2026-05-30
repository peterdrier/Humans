using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Coverage for ProfileController.DietaryMedical POST's signup-replay branches.
/// After a successful dietary save, the controller branches on
/// <c>ReturnAction</c> (carried via hidden form fields, originally injected as
/// query-string by the dietary gate in ShiftsController) and either replays the
/// original signup (single shift or range) or falls through to the default
/// "saved" flash.
///
/// The replay path is load-bearing because without it the redirect-then-replay
/// flow strands the user on Home/Index with no signup performed, which is the
/// exact UX failure the dietary-prompt-tightening spec exists to prevent. The
/// "replay failed but dietary save persisted" test pins down that the dietary
/// save is NOT rolled back on signup-replay failure — the user can retry the
/// signup directly from /Shifts without re-entering dietary info.
/// </summary>
public class ProfileControllerDietaryMedicalReplayTests
{
    private readonly UserManager<User> _userManager;
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly ProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileControllerDietaryMedicalReplayTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var configuration = Substitute.For<IConfiguration>();
        var authorizationService = Substitute.For<IAuthorizationService>();
        authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        _controller = new ProfileController(
            _userService,
            _userManager,
            Substitute.For<IProfilePictureService>(),
            _profileEditor,
            Substitute.For<IContactFieldService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IEmailMessageFactory>(),
            Substitute.For<IUserEmailService>(),
            Substitute.For<ICommunicationPreferenceService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IOnboardingService>(),
            Substitute.For<IHumanLifecycleService>(),
            Substitute.For<IRoleAssignmentService>(),
            _signupService,
            _shiftMgmt,
            Substitute.For<IShiftView>(),
            Substitute.For<IGdprExportService>(),
            configuration,
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            new FakeClock(Instant.FromUtc(2026, 5, 25, 12, 0)),
            authorizationService,
            Substitute.For<IConsentServiceRead>(),
            Substitute.For<IApplicationDecisionService>(),
            Substitute.For<IAccountDeletionService>(),
            Substitute.For<IMembershipCalculator>(),
            Substitute.For<SignInManager<User>>(
                _userManager,
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IUserClaimsPrincipalFactory<User>>(),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<User>>.Instance,
                Substitute.For<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>(),
                Substitute.For<IUserConfirmation<User>>()),
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
        }, authenticationType: "TestAuth");

        // SetError on HumansControllerBase resolves ILoggerFactory off
        // HttpContext.RequestServices and reads ControllerContext.ActionDescriptor.ActionName
        // for its debug log; wire both so the signup-replay-fails path doesn't NRE.
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services.BuildServiceProvider(),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "DietaryMedical" },
        };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        // RedirectToAction reads ControllerBase.Url, which otherwise resolves
        // IUrlHelperFactory off RequestServices. Stub it so the redirect
        // branches succeed without a full MVC routing context.
        _controller.Url = Substitute.For<IUrlHelper>();

        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(
                user: new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" },
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: null,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));

        // Happy-path scaffolding for the dietary save itself — every test
        // except the validation-failure case needs these to reach the
        // replay-switch.
        _shiftMgmt.GetOrCreateShiftProfileAsync(_userId)
            .Returns(new VolunteerEventProfile { UserId = _userId });
    }

    [HumansFact]
    public async Task Post_ValidSave_ReturnActionSignup_CallsSignupService()
    {
        var shiftId = Guid.NewGuid();
        _signupService.SignUpAsync(_userId, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var model = MakeValidModel();
        model.ReturnAction = "signup";
        model.ShiftId = shiftId;

        var result = await _controller.DietaryMedical(model);

        await _profileEditor.Received(1).SaveDietaryMedicalAsync(_userId, Arg.Any<UserProfileDietaryMedicalCommand>());
        await _signupService.Received(1)
            .SignUpAsync(_userId, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>());
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Shifts");
    }

    [HumansFact]
    public async Task Post_ValidSave_ReturnActionSignupRange_CallsRangeSignup()
    {
        var rotaId = Guid.NewGuid();
        _signupService.SignUpRangeAsync(
                _userId,
                rotaId,
                0,
                2,
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var model = MakeValidModel();
        model.ReturnAction = "signuprange";
        model.RotaId = rotaId;
        model.StartDayOffset = 0;
        model.EndDayOffset = 2;

        var result = await _controller.DietaryMedical(model);

        await _profileEditor.Received(1).SaveDietaryMedicalAsync(_userId, Arg.Any<UserProfileDietaryMedicalCommand>());
        await _signupService.Received(1)
            .SignUpRangeAsync(
                _userId,
                rotaId,
                0,
                2,
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>());
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Shifts");
    }

    [HumansFact]
    public async Task Post_ValidSave_ReturnActionShifts_RedirectsToShifts_NoSignupReplay()
    {
        var model = MakeValidModel();
        model.ReturnAction = "shifts";

        var result = await _controller.DietaryMedical(model);

        await _profileEditor.Received(1).SaveDietaryMedicalAsync(_userId, Arg.Any<UserProfileDietaryMedicalCommand>());
        await _signupService.DidNotReceiveWithAnyArgs()
            .SignUpAsync(default, default, default, default);
        await _signupService.DidNotReceiveWithAnyArgs()
            .SignUpRangeAsync(default, default, default, default, default, default);
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Shifts");
    }

    [HumansFact]
    public async Task Post_ValidSave_NoReturnAction_RedirectsToHome()
    {
        var model = MakeValidModel();
        // ReturnAction left null — pre-feature behaviour.

        var result = await _controller.DietaryMedical(model);

        await _profileEditor.Received(1).SaveDietaryMedicalAsync(_userId, Arg.Any<UserProfileDietaryMedicalCommand>());
        await _signupService.DidNotReceiveWithAnyArgs()
            .SignUpAsync(default, default, default, default);
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Home");
    }

    [HumansFact]
    public async Task Post_ValidationFails_DoesNotReplay()
    {
        // ModelState invalid before the action runs — the early bail-out
        // must not touch the dietary save OR the signup-replay path.
        _controller.ModelState.AddModelError("DietaryPreference", "Required");

        var model = MakeValidModel();
        model.ReturnAction = "signup";
        model.ShiftId = Guid.NewGuid();

        var result = await _controller.DietaryMedical(model);

        result.Should().BeOfType<ViewResult>();
        await _profileEditor.DidNotReceiveWithAnyArgs()
            .SaveDietaryMedicalAsync(default, default!, default);
        await _signupService.DidNotReceiveWithAnyArgs()
            .SignUpAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task Post_SignupReplay_Fails_StillRedirectsToShifts_DietarySavePersisted()
    {
        // Signup-replay failure must NOT roll back the dietary save — the user
        // lands on /Shifts with the signup error flash and can retry the signup
        // directly without re-entering dietary info.
        var shiftId = Guid.NewGuid();
        _signupService.SignUpAsync(_userId, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Fail("Shift full"));

        var model = MakeValidModel();
        model.ReturnAction = "signup";
        model.ShiftId = shiftId;

        var result = await _controller.DietaryMedical(model);

        await _profileEditor.Received(1).SaveDietaryMedicalAsync(_userId, Arg.Any<UserProfileDietaryMedicalCommand>());
        await _signupService.Received(1)
            .SignUpAsync(_userId, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>());
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Shifts");
    }

    private static DietaryMedicalViewModel MakeValidModel() => new()
    {
        DietaryPreference = "Omnivore",
    };
}
