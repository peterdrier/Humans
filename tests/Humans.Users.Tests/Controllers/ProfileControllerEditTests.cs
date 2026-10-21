using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Humans.Base.Extensions;
using Humans.Users.Controllers;
using Humans.Users.Services;
using Humans.Users.Models;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Tickets.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;

using Humans.Users.Tests.Infrastructure;
using Humans.Base;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

using Humans.GoogleIntegration.Contracts;

namespace Humans.Users.Tests.Controllers;

/// <summary>
/// Initial-setup tier-application dispatch on <c>ProfileController.Edit</c> POST.
/// The no-duplicate guard is the critical path: a replayed form must not submit twice.
/// </summary>
public class ProfileControllerEditTests
{
    private readonly IProfileEditorService _profileEditorService = Substitute.For<IProfileEditorService>();
    private readonly IContactFieldService _contactFields = Substitute.For<IContactFieldService>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IGovernanceService _governanceService =
        Substitute.For<IGovernanceService>();
    private readonly IOnboardingIntake _onboardingService = Substitute.For<IOnboardingIntake>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly ProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _profileId = Guid.NewGuid();

    public ProfileControllerEditTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        var userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var localizer = Substitute.For<IStringLocalizer<UsersResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();
        sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        sharedLocalizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // The Edit POST reads "GoogleMaps:ApiKey" only on validation-failure
        // branches; stub it so a stray re-render doesn't NRE.
        _configuration["GoogleMaps:ApiKey"].Returns("test-key");

        _controller = new ProfileController(
            _userService,
            userManager,
            _profileEditorService,
            _contactFields,
            _onboardingService,
            _configuration,
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            sharedLocalizer,
            _governanceService,
            _accountDeletionService,
            _membershipCalculator,
            ConsentReviewStubs.Create());

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
        // also call IUserManagement.GetUserInfoAsync. Default stub returns a UserInfo
        // with no profile so the initial-setup branch is taken; per-test overrides
        // (e.g. approved profile) replace it.
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" }
                    .ToUserInfo()));

        // SaveProfileAsync is invoked unconditionally by the happy path.
        _profileEditorService.SaveProfileAsync(
                Arg.Any<Guid>(), Arg.Any<ProfileSaveRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProfileEditResult(true, _profileId));
        _userService.SaveProfileLanguagesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ProfileLanguageInfo>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserProfileLanguagesSaveResult(true, _userId)));

        // The happy path now also writes meal-pref + allergies onto the shift
        // profile. Return a fresh profile by default so existing tests don't NRE
        // when the controller sets fields on it.
    }

    [HumansTheory]
    [InlineData("missingType", "BoardOnly")]
    [InlineData("missingVisibility", "BoardOnly")]
    [InlineData("retired", "BoardOnly")]
    [InlineData("valid", "BoardOnly")]
    [InlineData("valid", "AllActiveProfiles")]
    public async Task ContactRows_require_current_type_and_explicit_visibility_before_profile_save(string scenario, string visibility)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        _controller.HttpContext.RequestServices = services;
        _controller.Url = Substitute.For<IUrlHelper>();
        var contactId = Guid.NewGuid();
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["BurnerName"] = "Burner",
            ["FirstName"] = "First",
            ["LastName"] = "Last",
            ["NoPriorBurnExperience"] = "true",
            ["DietaryPreference"] = "omnivore",
            ["EditableContactFields[0].Id"] = contactId.ToString(),
            ["EditableContactFields[0].FieldType"] = string.Equals(scenario, "retired", StringComparison.Ordinal) ? "0" : "Signal",
            ["EditableContactFields[0].Value"] = "contact-handle",
            ["EditableContactFields[0].Visibility"] = visibility
        };
        var field = string.Equals(scenario, "missingVisibility", StringComparison.Ordinal) ? "Visibility" : "FieldType";
        if (scenario.StartsWith("missing", StringComparison.Ordinal))
            fields.Remove($"EditableContactFields[0].{field}");
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(ProfileViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(ProfileViewModel)
        });
        var context = DefaultModelBindingContext.CreateBindingContext(_controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var model = (ProfileViewModel)context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(_controller.ControllerContext, null, "", model);

        var result = await _controller.Edit(model);

        if (!string.Equals(scenario, "valid", StringComparison.Ordinal))
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            _controller.ModelState[$"EditableContactFields[0].{field}"]!.Errors.Should().ContainSingle();
            _profileEditorService.ReceivedCalls().Should().BeEmpty();
            _contactFields.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            _controller.ModelState.IsValid.Should().BeTrue();
            var expectedVisibility = Enum.Parse<ContactFieldVisibility>(visibility);
            await _contactFields.Received(1).SaveContactFieldsAsync(_profileId,
                Arg.Is<IReadOnlyList<ContactFieldEditDto>>(rows => rows.Count == 1 && rows[0].Id == contactId
                    && rows[0].FieldType == ContactFieldType.Signal && rows[0].Visibility == expectedVisibility), CancellationToken.None);
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_contact_removal_ids_are_bound_before_any_profile_write(bool malformed)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        _controller.HttpContext.RequestServices = services;
        _controller.Url = Substitute.For<IUrlHelper>();
        var legacyId = Guid.NewGuid();
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["BurnerName"] = "Burner",
            ["FirstName"] = "First",
            ["LastName"] = "Last",
            ["NoPriorBurnExperience"] = "true",
            ["DietaryPreference"] = "omnivore",
            ["RemovedLegacyContactFieldIds"] = malformed ? "not-a-guid" : legacyId.ToString()
        };
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(ProfileViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(ProfileViewModel)
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(_controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (ProfileViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(_controller.ControllerContext, binding.ValidationState, "", model);

        var result = await _controller.Edit(model);

        if (malformed)
        {
            result.Should().BeOfType<ViewResult>();
            _controller.ModelState["RemovedLegacyContactFieldIds"]!.Errors.Should().ContainSingle();
            _profileEditorService.ReceivedCalls().Should().BeEmpty();
            _contactFields.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await _contactFields.Received(1).SaveContactFieldsAsync(_profileId,
                Arg.Is<IReadOnlyList<ContactFieldEditDto>>(rows => rows.Count == 0), CancellationToken.None,
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(legacyId)));
        }
    }

    [HumansFact]
    public async Task Invalid_profile_edit_reloads_only_owners_legacy_contacts()
    {
        var legacy = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = _profileId,
            FieldType = default,
            Value = "legacy@example.org",
            Visibility = ContactFieldVisibility.BoardOnly
        };
        var info = UserInfoFactory.Create(new User { Id = _userId, PreferredLanguage = "en" }, [], [], [],
            new Profile { Id = _profileId, UserId = _userId, FirstName = "First", LastName = "Last" }, [legacy], [], [], []);
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(info));
        var model = MakeValidModel();
        model.ContactFields = [new() { Id = Guid.NewGuid(), Value = "untrusted@example.org" }];
        _controller.ModelState.AddModelError("BurnerName", "Required");

        var result = await _controller.Edit(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        model.ContactFields.Should().ContainSingle().Which.Id.Should().Be(legacy.Id);
        model.ContactFields[0].Value.Should().Be("legacy@example.org");
        _profileEditorService.ReceivedCalls().Should().BeEmpty();
        _contactFields.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void MemberProfileForms_LocalizeValidationMessages(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();

        void AssertErrors(object model, params (string Field, string Key, object[] Arguments)[] errors)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", model);
            foreach (var (field, key, arguments) in errors)
            {
                var expected = localizer[key, arguments];
                expected.ResourceNotFound.Should().BeFalse();
                context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
            }
        }

        AssertErrors(new ProfileViewModel(),
            ("BurnerName", "Validation_Required", []),
            ("FirstName", "Validation_Required", []),
            ("LastName", "Validation_Required", []));
        AssertErrors(new ProfileViewModel
        {
            BurnerName = new string('x', 101),
            FirstName = new string('x', 101),
            LastName = new string('x', 101),
            City = new string('x', 257),
            CountryCode = new string('x', 3),
            PlaceId = new string('x', 513),
            Bio = new string('x', 1001),
            Pronouns = new string('x', 101),
            ContributionInterests = new string('x', 2001),
            BoardNotes = new string('x', 2001),
            EmergencyContactName = new string('x', 257),
            EmergencyContactPhone = new string('x', 51),
            EmergencyContactRelationship = new string('x', 101),
            BirthdayMonth = 13,
            BirthdayDay = 32,
        },
            ("BurnerName", "Validation_MaxLength", ["", 100]),
            ("FirstName", "Validation_MaxLength", ["", 100]),
            ("LastName", "Validation_MaxLength", ["", 100]),
            ("City", "Validation_MaxLength", ["", 256]),
            ("CountryCode", "Validation_MaxLength", ["", 2]),
            ("PlaceId", "Validation_MaxLength", ["", 512]),
            ("Bio", "Validation_MaxLength", ["", 1000]),
            ("Pronouns", "Validation_MaxLength", ["", 100]),
            ("ContributionInterests", "Validation_MaxLength", ["", 2000]),
            ("BoardNotes", "Validation_MaxLength", ["", 2000]),
            ("EmergencyContactName", "Validation_MaxLength", ["", 256]),
            ("EmergencyContactPhone", "Validation_MaxLength", ["", 50]),
            ("EmergencyContactRelationship", "Validation_MaxLength", ["", 100]),
            ("BirthdayMonth", "Validation_Range", ["", 1, 12]),
            ("BirthdayDay", "Validation_Range", ["", 1, 31]));
        AssertErrors(new MedicalViewModel { MedicalConditions = new string('x', 4001) },
            ("MedicalConditions", "Validation_MaxLength", ["", 4000]));
        AssertErrors(new ProfileViewModel
        {
            EditableContactFields = [new() { CustomLabel = new string('x', 101) }, new() { Value = new string('x', 501) }],
            EditableVolunteerHistory = [new(), new() { DateString = "2026-01", EventName = new string('x', 257), Description = new string('x', 2001) }],
            EditableLanguages = [new(), new() { LanguageCode = new string('x', 11) }],
        },
            ("EditableContactFields[0].CustomLabel", "Validation_MaxLength", ["", 100]),
            ("EditableContactFields[0].Value", "Validation_Required", []),
            ("EditableContactFields[1].Value", "Validation_MaxLength", ["", 500]),
            ("EditableVolunteerHistory[0].DateString", "Validation_Required", []),
            ("EditableVolunteerHistory[0].EventName", "Validation_Required", []),
            ("EditableVolunteerHistory[1].EventName", "Validation_MaxLength", ["", 256]),
            ("EditableVolunteerHistory[1].Description", "Validation_MaxLength", ["", 2000]),
            ("EditableLanguages[0].LanguageCode", "Validation_Required", []),
            ("EditableLanguages[1].LanguageCode", "Validation_MaxLength", ["", 10]));
        AssertErrors(new ContactFieldEditViewModel
        {
            FieldType = (ContactFieldType)0,
            Value = "handle",
            Visibility = ContactFieldVisibility.BoardOnly
        }, ("FieldType", "Validation_InvalidValue", []));
        AssertErrors(new EmailsViewModel { NewEmail = "invalid" },
            ("NewEmail", "Validation_EmailAddress", []));
        AssertErrors(new EmailsViewModel { NewEmail = new string('x', 245) + "@example.com" },
            ("NewEmail", "Validation_MaxLength", ["", 256]));
    }

    [HumansFact]
    public void Edit_caps_the_profile_picture_request_before_multipart_binding()
    {
        var limit = typeof(ProfileController).GetMethods()
            .Single(method => string.Equals(method.Name, nameof(ProfileController.Edit), StringComparison.Ordinal)
                && method.GetCustomAttribute<HttpPostAttribute>() is not null)
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(21L * 1024 * 1024);
    }

    [HumansFact]
    public async Task MedicalPage_StopsLoadingAfterRequestCancellation()
    {
        using var request = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = request.Token;
        async ValueTask<UserInfo?> ReadViewerAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new User { Id = _userId, PreferredLanguage = "en" }.ToUserInfo();
        }
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(call => ReadViewerAsync(call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> load = () => _controller.Medical(request.Token);
        (await load()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Edit_InitialSetup_SavesProfileWithoutTouchingTierApplications()
    {
        // Tier applications left the edit form for /Governance/Applications/Create
        // (Users decomposition commit 11): saving the profile never reads or writes them.
        var model = MakeValidModel();

        await _controller.Edit(model);

        await _governanceService.DidNotReceiveWithAnyArgs()
            .GetUserApplicationsAsync(Guid.Empty, Arg.Any<CancellationToken>());
        // CV rides on the save request — SaveProfileAsync owns persisting it.
        await _profileEditorService.Received(1).SaveProfileAsync(
            _userId, Arg.Is<ProfileSaveRequest>(r => r.VolunteerHistory != null),
            Arg.Any<CancellationToken>());
        await _userService.Received(1).SaveProfileLanguagesAsync(
            _profileId, Arg.Any<IReadOnlyList<ProfileLanguageInfo>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_InitialSetup_TriggersOnboardingConsentCheck()
    {
        var model = MakeValidModel();

        await _controller.Edit(model);

        await _onboardingService.Received(1)
            .SetConsentCheckPendingIfEligibleAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_InitialSetup_DeletionPending_CancelsDeletion()
    {
        // User in pending-deletion state: completing the profile must cancel the deletion.
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en", DeletionRequestedAt = SystemClock.Instance.GetCurrentInstant() }
                    .ToUserInfo()));

        var model = MakeValidModel();

        await _controller.Edit(model);

        await _accountDeletionService.Received(1).CancelDeletionAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_Post_ServiceRefusal_UsesExistingLocalizedFeedbackAndStopsAfterSaveEffects()
    {
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);
        _profileEditorService.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProfileEditResult(false, ErrorKey: "Profile_BurnerCVRequired"));
        var model = MakeValidModel();

        var result = await _controller.Edit(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        _controller.ModelState[nameof(ProfileViewModel.NoPriorBurnExperience)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Profile_BurnerCVRequired");
        await _userService.DidNotReceiveWithAnyArgs().SaveProfileLanguagesAsync(default, default!, default);
        await _onboardingService.DidNotReceiveWithAnyArgs().SetConsentCheckPendingIfEligibleAsync(default, default);
    }

    // ─── /Profile/Me/Medical (GDPR Art. 9, owner-only) ─────────────────────────

    [HumansFact]
    public async Task Medical_Get_ShowsOnlyTheSignedInMembersOwnConditions()
    {
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(new Profile { UserId = _userId, BurnerName = "Burner", MedicalConditions = "Epilepsy" }));

        var result = await _controller.Medical(CancellationToken.None);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<MedicalViewModel>()
            .Which.MedicalConditions.Should().Be("Epilepsy");
        await _userService.Received().GetUserInfoAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("  Diabetes  ", "Diabetes")]
    [InlineData("   ", null)]
    public async Task Medical_Post_SavesTheTrimmedConditions_ForTheSignedInMemberOnly(string posted, string? saved)
    {
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(new Profile { UserId = _userId, BurnerName = "Burner" }));

        var result = await _controller.Medical(new MedicalViewModel { MedicalConditions = posted });

        await _userService.Received(1).SaveMedicalConditionsAsync(_userId, saved, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(ProfileController.Index));
    }

    [HumansFact]
    public async Task Medical_Post_InvalidModel_DoesNotSave()
    {
        _controller.ModelState.AddModelError(nameof(MedicalViewModel.MedicalConditions), "Validation_MaxLength");

        var result = await _controller.Medical(new MedicalViewModel { MedicalConditions = "x" });

        result.Should().BeOfType<ViewResult>();
        await _userService.DidNotReceiveWithAnyArgs().SaveMedicalConditionsAsync(default, default, default);
    }

    [HumansFact]
    public void Medical_Post_RequiresAntiforgery()
    {
        typeof(ProfileController).GetMethods()
            .Single(m => string.Equals(m.Name, nameof(ProfileController.Medical), StringComparison.Ordinal)
                && m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
    }

    private static ProfileViewModel MakeValidModel() => new()
    {
        BurnerName = "Burner",
        FirstName = "First",
        LastName = "Last",
        // NoPriorBurnExperience=true short-circuits the volunteer-history
        // requirement so we don't need to fabricate history rows just to
        // reach the save path under test.
        NoPriorBurnExperience = true,
    };

    private UserInfo BuildUserInfo(Profile? profile) => UserInfoFactory.Create(
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
