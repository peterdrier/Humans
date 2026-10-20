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
using Humans.Gdpr.Contracts;
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
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IGovernanceService _governanceService =
        Substitute.For<IGovernanceService>();
    private readonly IGovernanceWrite _governanceWrite =
        Substitute.For<IGovernanceWrite>();
    private readonly IOnboardingIntake _onboardingService = Substitute.For<IOnboardingIntake>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();
    private readonly IGdprService _gdprService = Substitute.For<IGdprService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
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
            _commPrefService,
            _onboardingService,
            Substitute.For<IShiftSignups>(),
            _settings,
            _gdprService,
            _configuration,
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            sharedLocalizer,
            new FakeClock(Instant.FromUtc(2026, 5, 9, 12, 0)),
            _governanceService,
            _governanceWrite,
            _accountDeletionService,
            _membershipCalculator);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString())
        ], authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());

        userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new User { Id = _userId, DisplayName = "Test Human", PreferredLanguage = "en" });
        userManager.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(_userId.ToString());

        // Tier-application field rules live in IGovernanceWrite and are
        // pre-flighted by Edit POST before any save. Default to "passes" so tests
        // that aren't about tier validation reach the save path; the tier-validation
        // tests below override this with the specific failure they exercise.
        _governanceWrite
            .ValidateSubmission(
                Arg.Any<MembershipTier>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new ApplicationDecisionResult(true));

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
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(),
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
        var model = MakeValidModel(MembershipTier.Volunteer);
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
    [InlineData("emailEnabled", true)]
    [InlineData("alertEnabled", true)]
    [InlineData("emailEnabled", false)]
    [InlineData("alertEnabled", false)]
    public async Task UpdatePreference_InvalidBinding_DoesNotChangeChannels(string field, bool viewerExists)
    {
        if (!viewerExists)
            _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        _controller.ModelState.AddModelError(field, "Not a boolean.");
        var result = await _controller.UpdatePreference(MessageCategory.Marketing,
            !string.Equals(field, "emailEnabled", StringComparison.Ordinal),
            !string.Equals(field, "alertEnabled", StringComparison.Ordinal));

        if (viewerExists) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(_commPrefService.ReceivedCalls());
    }

    [HumansTheory]
    [InlineData(typeof(ProfileController), "emailEnabled")]
    [InlineData(typeof(ProfileController), "alertEnabled")]
    [InlineData(typeof(GuestAccountController), "emailEnabled")]
    [InlineData(typeof(GuestAccountController), "alertEnabled")]
    public async Task CommunicationPreferenceFlags_MissingFromForm_AreBindingErrors(Type controllerType, string field)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var context = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
        var parameter = controllerType.GetMethod("UpdatePreference")!.GetParameters().Single(p => string.Equals(p.Name, field, StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var descriptor = new ControllerParameterDescriptor
        {
            Name = field,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter
        };
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter
        });

        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["category"] = "Marketing",
            [field is "emailEnabled" ? "alertEnabled" : "emailEnabled"] = "true"
        };
        await services.GetRequiredService<ParameterBinder>().BindModelAsync(context, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(values), CultureInfo.InvariantCulture),
            descriptor, metadata, value: null);

        Assert.False(context.ModelState.IsValid);
        Assert.True(context.ModelState.ContainsKey(field));
    }

    [HumansFact]
    public async Task UpdatePreference_ExplicitFalseFlags_RemainValid()
    {
        Assert.IsType<OkResult>(await _controller.UpdatePreference(MessageCategory.Marketing, false, false));
        await _commPrefService.Received(1).UpdatePreferenceAsync(_userId, MessageCategory.Marketing,
            true, false, "Profile", actorUserId: _userId, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Me_AbandonedRequest_CancelsOnsiteYearRead()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _membershipCalculator.GetMembershipSnapshotAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 0, 0, []));
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettingsInfo?)null);
        _settings.GetActiveEventSettingsAsync(aborted.Token)
            .Returns(Task.FromCanceled<EventSettingsInfo?>(aborted.Token));

        var act = () => _controller.Me(aborted.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
            ApplicationMotivation = new string('x', 2001),
            ApplicationAdditionalInfo = new string('x', 1001),
            ApplicationSignificantContribution = new string('x', 2001),
            ApplicationRoleUnderstanding = new string('x', 2001),
            AllergyOtherText = new string('x', 501),
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
            ("ApplicationMotivation", "Validation_MaxLength", ["", 2000]),
            ("ApplicationAdditionalInfo", "Validation_MaxLength", ["", 1000]),
            ("ApplicationSignificantContribution", "Validation_MaxLength", ["", 2000]),
            ("ApplicationRoleUnderstanding", "Validation_MaxLength", ["", 2000]),
            ("AllergyOtherText", "Validation_MaxLength", ["", 500]),
            ("BirthdayMonth", "Validation_Range", ["", 1, 12]),
            ("BirthdayDay", "Validation_Range", ["", 1, 31]));
        AssertErrors(new DietaryMedicalViewModel
        {
            AllergyOtherText = new string('x', 501),
            IntoleranceOtherText = new string('x', 501),
            MedicalConditions = new string('x', 4001),
        },
            ("DietaryPreference", "Validation_Required", []),
            ("AllergyOtherText", "Validation_MaxLength", ["", 500]),
            ("IntoleranceOtherText", "Validation_MaxLength", ["", 500]),
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
        AssertErrors(new SendMessageViewModel(), ("Message", "Validation_Required", []));
        AssertErrors(new SendMessageViewModel { Message = new string('x', 2001) },
            ("Message", "Validation_MaxLength", ["", 2000]));
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

    [HumansTheory]
    [InlineData("Privacy")]
    [InlineData("DietaryMedical")]
    [InlineData("CommunicationPreferences")]
    public async Task ProfileReadPages_StopLoadingAfterRequestCancellation(string page)
    {
        using var request = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = request.Token;
        async ValueTask<UserInfo?> ReadViewer(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new User { Id = _userId, PreferredLanguage = "en" }.ToUserInfo();
        }
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(call => ReadViewer(call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> load = page switch
        {
            "Privacy" => () => _controller.Privacy(),
            "DietaryMedical" => () => _controller.DietaryMedical(),
            _ => () => _controller.CommunicationPreferences(),
        };
        (await load()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountWallPages_StopLoadingAfterRequestCancellation(bool deletion)
    {
        using var request = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = request.Token;
        ((ClaimsIdentity)_controller.User.Identity!).AddClaim(
            new Claim(Humans.Base.Authorization.RoleChecks.UserStateClaimType, nameof(UserState.Suspended)));
        var now = Instant.FromUtc(2026, 10, 3, 0, 0);
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(new User
            {
                Id = _userId,
                PreferredLanguage = "en",
                DeletionRequestedAt = now,
                DeletionScheduledFor = now + Duration.FromDays(30),
            }.ToUserInfo());
        });
        var wall = new UserController(_userService, _accountDeletionService, Substitute.For<IStringLocalizer<UsersResource>>())
        {
            ControllerContext = _controller.ControllerContext,
        };
        Func<Task<IActionResult>> load = deletion ? wall.Deletion : wall.Status;
        (await load()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task DeletionPages_report_the_later_of_grace_deadline_and_ticket_hold(int? holdDays)
    {
        var now = Instant.FromUtc(2026, 10, 3, 0, 0);
        var user = new User
        {
            Id = _userId,
            DeletionRequestedAt = now,
            DeletionScheduledFor = now + Duration.FromDays(30),
            DeletionEligibleAfter = holdDays is { } days ? now + Duration.FromDays(days) : null,
        }.ToUserInfo();
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        var expectedDate = (now + Duration.FromDays(Math.Max(30, holdDays ?? 0))).ToDateTimeUtc();

        var privacy = (await _controller.Privacy()).Should().BeOfType<ViewResult>().Subject;
        privacy.Model.Should().BeOfType<PrivacyViewModel>().Which.DeletionScheduledFor.Should().Be(expectedDate);

        var wall = new UserController(_userService, _accountDeletionService, Substitute.For<IStringLocalizer<UsersResource>>())
        {
            ControllerContext = _controller.ControllerContext,
        };
        var deletion = (await wall.Deletion()).Should().BeOfType<ViewResult>().Subject;
        deletion.Model.Should().BeOfType<PendingDeletionViewModel>().Which.ScheduledFor.Should().Be(expectedDate);
    }

    [HumansFact]
    public async Task DownloadData_ExportFailure_ShowsErrorAndReturnsToPrivacy()
    {
        _gdprService.ExportForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GdprExport>(new InvalidOperationException("contributor failed")));

        var result = await _controller.DownloadData(TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Privacy");
        _controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Error_TryAgainLater");
    }

    [HumansFact]
    public async Task DownloadData_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _gdprService.ExportForUserAsync(_userId, cancellation.Token)
            .Returns(Task.FromException<GdprExport>(new OperationCanceledException(cancellation.Token)));

        var act = () => _controller.DownloadData(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Edit_InitialSetup_Volunteer_DoesNotDispatchTierApplication()
    {

        var model = MakeValidModel(MembershipTier.Volunteer);

        await _controller.Edit(model);

        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, Arg.Any<CancellationToken>());
        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, Arg.Any<CancellationToken>());
        // CV rides on the save request — SaveProfileAsync owns persisting it.
        await _profileEditorService.Received(1).SaveProfileAsync(
            _userId, Arg.Any<string>(),
            Arg.Is<ProfileSaveRequest>(r => r.VolunteerHistory != null),
            Arg.Any<CancellationToken>());
        await _userService.Received(1).SaveProfileLanguagesAsync(
            _profileId, Arg.Any<IReadOnlyList<ProfileLanguageInfo>>(), Arg.Any<CancellationToken>());
    }

    // The tier-application field rules live in IGovernanceWrite (the service that
    // owns tier-application state), not in a controller-private helper — see
    // memory/architecture/no-business-logic-in-controllers.md. The controller's remaining job
    // is to pre-flight them before any save and map the ErrorKey onto a localized,
    // form-field-targeted ModelState error, the same way GovernanceApplicationsController does.
    [HumansTheory]
    [InlineData("MotivationRequired", nameof(ProfileViewModel.ApplicationMotivation))]
    [InlineData("MotivationLength", nameof(ProfileViewModel.ApplicationMotivation))]
    [InlineData("AdditionalInfoTooLong", nameof(ProfileViewModel.ApplicationAdditionalInfo))]
    [InlineData("SignificantContributionTooLong", nameof(ProfileViewModel.ApplicationSignificantContribution))]
    [InlineData("RoleUnderstandingTooLong", nameof(ProfileViewModel.ApplicationRoleUnderstanding))]
    [InlineData("SignificantContributionRequired", nameof(ProfileViewModel.ApplicationSignificantContribution))]
    [InlineData("RoleUnderstandingRequired", nameof(ProfileViewModel.ApplicationRoleUnderstanding))]
    public async Task Edit_InitialSetup_TierValidationFailure_RerendersWithFieldError_AndDoesNotSave(
        string errorKey, string expectedField)
    {
        _governanceWrite
            .ValidateSubmission(
                Arg.Any<MembershipTier>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new ApplicationDecisionResult(false, errorKey));

        var model = MakeValidModel(MembershipTier.Asociado, motivation: "to help");

        var result = await _controller.Edit(model);

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.Should().ContainKey(expectedField);

        // Validation runs before persistence so a rejected submit cannot half-save.
        await _profileEditorService.DidNotReceiveWithAnyArgs()
            .SaveProfileAsync(Guid.Empty, null!, null!, Arg.Any<CancellationToken>());
        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_InitialSetup_Colaborador_NoExistingApp_CallsSubmitAsync()
    {
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Colaborador, motivation: "to help");

        await _controller.Edit(model);

        await _governanceWrite.Received(1).SubmitAsync(
            _userId, MembershipTier.Colaborador, "to help",
            Arg.Any<string?>(),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, Arg.Any<CancellationToken>());
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
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([existingDraft]);

        var model = MakeValidModel(MembershipTier.Colaborador, motivation: "updated motivation");

        await _controller.Edit(model);

        await _governanceWrite.Received(1).UpdateDraftApplicationAsync(
            existingDraftId, MembershipTier.Colaborador, "updated motivation",
            Arg.Any<string?>(),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>());
        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, Arg.Any<CancellationToken>());
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

        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .SubmitAsync(Guid.Empty, default, null!, null, null, null, null!, Arg.Any<CancellationToken>());
        await _governanceWrite.DidNotReceiveWithAnyArgs()
            .UpdateDraftApplicationAsync(Guid.Empty, default, null!, null, null, null, Arg.Any<CancellationToken>());
        await _governanceService.DidNotReceiveWithAnyArgs()
            .GetUserApplicationsAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_InitialSetup_TriggersOnboardingConsentCheck()
    {
        var model = MakeValidModel(MembershipTier.Volunteer);

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

        var model = MakeValidModel(MembershipTier.Volunteer);

        await _controller.Edit(model);

        await _accountDeletionService.Received(1).CancelDeletionAsync(_userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_Post_WithDietary_WritesMealPrefAndAllergiesToProfile()
    {
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Volunteer);
        model.DietaryPreference = "Vegan";
        model.Allergies = ["Peanut", "Other"];
        model.AllergyOtherText = "Kiwi";

        await _controller.Edit(model);

        // Meal-pref + allergies are persisted through the Profile save request.
        await _profileEditorService.Received(1).SaveProfileAsync(
            _userId,
            Arg.Any<string>(),
            Arg.Is<ProfileSaveRequest>(r =>
                r.DietaryPreference == "Vegan"
                && r.Allergies != null && r.Allergies.Contains("Peanut") && r.Allergies.Contains("Other")
                && r.AllergyOtherText == "Kiwi"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_Post_WithDietary_DoesNotTouchIntolerancesOrMedical()
    {
        // The Edit page owns only meal-pref + allergies (carried on ProfileSaveRequest,
        // which has no intolerance/medical fields). Intolerances + medical are written
        // only by the DietaryMedical page via SaveDietaryMedicalAsync — never from Edit.
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Volunteer);
        model.DietaryPreference = "Vegan";
        model.Allergies = ["Peanut"];

        await _controller.Edit(model);

        await _profileEditorService.Received(1).SaveProfileAsync(
            _userId, Arg.Any<string>(),
            Arg.Is<ProfileSaveRequest>(r => r.DietaryPreference == "Vegan"),
            Arg.Any<CancellationToken>());
        await _profileEditorService.DidNotReceiveWithAnyArgs()
            .SaveDietaryMedicalAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_Post_AllergyOtherWithoutText_IsInvalidAndDoesNotSaveProfile()
    {
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = MakeValidModel(MembershipTier.Volunteer);
        model.Allergies = ["Other"];
        model.AllergyOtherText = "   "; // whitespace-only triggers the required rule

        var result = await _controller.Edit(model);

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        _controller.ModelState.ContainsKey(nameof(model.AllergyOtherText)).Should().BeTrue();
        await _profileEditorService.DidNotReceiveWithAnyArgs()
            .SaveProfileAsync(default, default!, default!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_Post_ServiceRefusal_UsesExistingLocalizedFeedbackAndStopsAfterSaveEffects()
    {
        _governanceService.GetUserApplicationsAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);
        _profileEditorService.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProfileEditResult(false, ErrorKey: "Profile_BurnerCVRequired"));
        var model = MakeValidModel(MembershipTier.Volunteer);

        var result = await _controller.Edit(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        _controller.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Profile_BurnerCVRequired");
        await _userService.DidNotReceiveWithAnyArgs().SaveProfileLanguagesAsync(default, default!, default);
        await _onboardingService.DidNotReceiveWithAnyArgs().SetConsentCheckPendingIfEligibleAsync(default, default);
    }

    [HumansFact]
    public async Task Edit_Get_PopulatesDietaryFieldsAndPreservesTagReadCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(new Profile
            {
                UserId = _userId,
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                DietaryPreference = "Pescatarian",
                Allergies = ["Dairy", "Other"],
                AllergyOtherText = "Mango",
            }));

        var result = await _controller.Edit(ct: cancellation.Token);

        var viewModel = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ProfileViewModel>().Subject;
        viewModel.DietaryPreference.Should().Be("Pescatarian");
        viewModel.Allergies.Should().BeEquivalentTo(["Dairy", "Other"]);
        viewModel.AllergyOtherText.Should().Be("Mango");
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
