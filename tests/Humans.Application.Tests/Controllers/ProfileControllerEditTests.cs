using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
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
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Coverage for the initial-setup tier-application orchestration that moved
/// from <c>ProfileService.SaveProfileAsync</c> into <c>ProfileController.Edit</c>
/// POST under issue nobodies-collective/Humans#685. The four removed
/// <c>ProfileServiceTests</c> tests that exercised the old service-layer
/// dispatch are replaced here at the controller layer:
///   * Volunteer + initial setup → no Application created.
///   * Colaborador + initial setup, no existing app → SubmitAsync called.
///   * Colaborador + initial setup, existing draft → UpdateDraftApplicationAsync called.
///   * Approved profile (not initial setup) → tier dispatch skipped entirely.
/// The no-duplicate guard (no second SubmitAsync when a Submitted app exists) is
/// the critical path: prevents data integrity issues if the form is replayed.
/// </summary>
public class ProfileControllerEditTests
{
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IApplicationDecisionService _applicationDecisionService =
        Substitute.For<IApplicationDecisionService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly ProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileControllerEditTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        var userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // The Edit POST reads "GoogleMaps:ApiKey" only on validation-failure
        // branches; stub it so a stray re-render doesn't NRE.
        _configuration["GoogleMaps:ApiKey"].Returns("test-key");

        var authorizationService = Substitute.For<IAuthorizationService>();
        authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        _controller = new ProfileController(
            _userService,
            userManager,
            _profileService,
            Substitute.For<IContactFieldService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IUserEmailService>(),
            Substitute.For<ICommunicationPreferenceService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IOnboardingService>(),
            Substitute.For<IHumanLifecycleService>(),
            Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IShiftSignupService>(),
            _shiftMgmt,
            Substitute.For<IShiftView>(),
            Substitute.For<IGdprExportService>(),
            _configuration,
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketQueryService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeClock(Instant.FromUtc(2026, 5, 9, 12, 0)),
            authorizationService,
            Substitute.For<IConsentService>(),
            _applicationDecisionService,
            Substitute.For<IAccountDeletionService>(),
            Substitute.For<IMembershipCalculator>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<SignInManager<User>>(
                userManager,
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IUserClaimsPrincipalFactory<User>>(),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<User>>.Instance,
                Substitute.For<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>(),
                Substitute.For<IUserConfirmation<User>>()),
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString())
        ], authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());

        userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" });
        userManager.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(_userId.ToString());

        // Edit POST resolves the current user through GetCurrentUserInfoAsync
        // (cache-resident); subsequent setup-detection lookups in the action body
        // also call IUserService.GetUserInfoAsync. Default stub returns a UserInfo
        // with no profile so the initial-setup branch is taken; per-test overrides
        // (e.g. approved profile) replace it.
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" }
                    .ToUserInfo()));

        // SaveProfileAsync is invoked unconditionally by the happy path.
        _profileService.SaveProfileAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
    }

    [HumansFact]
    public async Task Edit_InitialSetup_Volunteer_DoesNotDispatchTierApplication()
    {

        var model = MakeValidModel(MembershipTier.Volunteer);

        await _controller.Edit(model);

        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, CancellationToken.None);
        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, CancellationToken.None);
    }

    [HumansFact]
    public async Task Edit_InitialSetup_Colaborador_NoExistingApp_CallsSubmitAsync()
    {
        _applicationDecisionService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Colaborador, motivation: "to help");

        await _controller.Edit(model);

        await _applicationDecisionService.Received(1).SubmitAsync(
            _userId, MembershipTier.Colaborador, "to help",
            Arg.Any<string?>(),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, CancellationToken.None);
    }

    [HumansFact]
    public async Task Edit_InitialSetup_ExistingSubmittedApp_CallsUpdateDraftAndNotSubmit()
    {
        // No-duplicate guard: when a Submitted application already exists,
        // the controller must update it in place (UpdateDraftApplicationAsync)
        // and never call SubmitAsync — otherwise the user can produce two
        // pending applications by replaying the form. ApplicationDecisionService
        // also rejects with AlreadyPending as a backstop, but the controller
        // shouldn't lean on that.

        var existingDraftId = Guid.NewGuid();
        var existingDraft = new UserApplicationSnapshot(
            existingDraftId,
            _userId,
            ApplicationStatus.Submitted,
            MembershipTier.Colaborador,
            SystemClock.Instance.GetCurrentInstant(),
            ResolvedAt: null,
            TermExpiresAt: null,
            Motivation: "old motivation",
            AdditionalInfo: null,
            SignificantContribution: null,
            RoleUnderstanding: null);
        _applicationDecisionService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([existingDraft]);

        var model = MakeValidModel(MembershipTier.Colaborador, motivation: "updated motivation");

        await _controller.Edit(model);

        await _applicationDecisionService.Received(1).UpdateDraftApplicationAsync(
            existingDraftId, MembershipTier.Colaborador, "updated motivation",
            Arg.Any<string?>(),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>());
        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, CancellationToken.None);
    }

    [HumansFact]
    public async Task Edit_ApprovedProfile_DoesNotDispatchTierApplication()
    {
        // Approved profile means the user is past initial setup. Tier radios
        // on the form are advisory only — the controller must skip the
        // dispatch entirely regardless of which tier the form arrives with.
        var approvedProfile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            BurnerName = "Existing",
            FirstName = "Existing",
            LastName = "Human",
            IsApproved = true,
        };
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(approvedProfile));

        var model = MakeValidModel(MembershipTier.Colaborador, motivation: "irrelevant");

        await _controller.Edit(model);

        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, CancellationToken.None);
        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, CancellationToken.None);
        await _applicationDecisionService.DidNotReceiveWithAnyArgs()
            .GetUserApplicationsAsync(Guid.Empty, CancellationToken.None);
    }

    [HumansFact]
    public async Task Edit_Post_WithShiftTagIds_CallsSetVolunteerTagPreferencesAsync()
    {
        _applicationDecisionService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var tagId1 = Guid.NewGuid();
        var tagId2 = Guid.NewGuid();
        var model = MakeValidModel(MembershipTier.Volunteer);
        model.EditableShiftTagIds = [tagId1, tagId2];

        await _controller.Edit(model);

        await _shiftMgmt.Received(1).SetVolunteerTagPreferencesAsync(
            _userId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(tagId1) && ids.Contains(tagId2)));
    }

    [HumansFact]
    public async Task Edit_Post_WithEmptyShiftTagIds_CallsSetVolunteerTagPreferencesAsyncWithEmptyList()
    {
        _applicationDecisionService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Volunteer);
        model.EditableShiftTagIds = [];

        await _controller.Edit(model);

        await _shiftMgmt.Received(1).SetVolunteerTagPreferencesAsync(
            _userId,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 0));
    }

    [HumansFact]
    public async Task Edit_Post_ValidationFailure_RepopulatesAllShiftTagsAndDoesNotCallSetPreferences()
    {
        var tag1 = new ShiftTagSummary(Guid.NewGuid(), "Heavy lifting");
        var tag2 = new ShiftTagSummary(Guid.NewGuid(), "Working in the sun");
        _shiftMgmt.GetTagsAsync(Arg.Any<string?>())
            .Returns(new List<ShiftTagSummary> { tag1, tag2 });

        // Force ModelState invalid before the action runs.
        _controller.ModelState.AddModelError("BurnerName", "Required");

        var model = MakeValidModel(MembershipTier.Volunteer);

        var result = await _controller.Edit(model);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var returnedModel = viewResult.Model.Should().BeOfType<ProfileViewModel>().Subject;
        returnedModel.AllShiftTags.Should().HaveCountGreaterThan(0);

        await _shiftMgmt.DidNotReceiveWithAnyArgs()
            .SetVolunteerTagPreferencesAsync(Guid.Empty, null!);
    }

    private static ProfileViewModel MakeValidModel(
        MembershipTier tier,
        string? motivation = null) => new()
        {
            BurnerName = "Burner",
            FirstName = "First",
            LastName = "Last",
            // NoPriorBurnExperience=true short-circuits the volunteer-history
            // requirement so we don't need to fabricate history rows just to
            // reach the dispatch block under test.
            NoPriorBurnExperience = true,
            SelectedTier = tier,
            ApplicationMotivation = motivation,
        };

    private UserInfo BuildUserInfo(Profile? profile) => UserInfo.Create(
        user: new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
