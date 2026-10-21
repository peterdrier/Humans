using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Xunit;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// <see cref="CommunicationPreferencesController"/>: the member's own
/// <c>/Profile/Me/CommunicationPreferences</c> page and its update endpoint, moved from Users'
/// <c>ProfileController</c> in Users decomposition commit 14 with the same URLs.
/// </summary>
public class CommunicationPreferencesControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceManagement _commPrefService = Substitute.For<ICommunicationPreferenceManagement>();
    private readonly IStringLocalizer<EmailResource> _localizer = Substitute.For<IStringLocalizer<EmailResource>>();
    private readonly CommunicationPreferencesController _controller;

    public CommunicationPreferencesControllerTests()
    {
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(Viewer()));

        _controller = new CommunicationPreferencesController(
            _userService, _commPrefService, NullLogger<CommunicationPreferencesController>.Instance, _localizer);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "test")),
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider(),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
        };
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
    }

    private UserInfo Viewer() => UserInfo.Create(
        new User { Id = _userId, DisplayName = "Human", PreferredLanguage = "en" }, [], [], [], profile: null, []);

    [HumansFact]
    public void Routes_KeepTheProfileUrls_AndRequireSignIn()
    {
        var type = typeof(CommunicationPreferencesController);
        type.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        type.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("Profile");
        type.GetMethod(nameof(CommunicationPreferencesController.Index))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("Me/CommunicationPreferences");
        var update = type.GetMethod(nameof(CommunicationPreferencesController.UpdatePreference))!;
        update.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("Me/CommunicationPreferences/Update");
        update.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        type.GetMethod(nameof(CommunicationPreferencesController.Notifications))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("Me/Notifications");
    }

    [HumansFact]
    public async Task Index_RendersForTheSignedInMember()
    {
        var result = await _controller.Index();

        result.Should().BeOfType<ViewResult>().Which.Model.Should().Be(_userId);
    }

    [HumansFact]
    public async Task Index_NoViewer_IsNotFound()
    {
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        (await _controller.Index()).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Index_StopsLoadingAfterRequestCancellation()
    {
        using var request = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = request.Token;
        async ValueTask<UserInfo?> ReadViewer(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Viewer();
        }
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(call => ReadViewer(call.Arg<CancellationToken>()));

        (await _controller.Index()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await FluentActions.Awaiting(() => _controller.Index()).Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public void Notifications_RedirectsPermanentlyToThePreferencesPage()
    {
        var result = _controller.Notifications().Should().BeOfType<RedirectToActionResult>().Subject;
        result.Permanent.Should().BeTrue();
        result.ActionName.Should().Be(nameof(CommunicationPreferencesController.Index));
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

    [HumansFact]
    public async Task UpdatePreference_AlwaysOnCategory_IsRejected()
    {
        var result = await _controller.UpdatePreference(MessageCategory.System, emailEnabled: false, alertEnabled: false);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().Be("Email_CommPrefs_AlwaysOnCategoryCannotChange");
        Assert.Empty(_commPrefService.ReceivedCalls());
    }

    [HumansFact]
    public async Task UpdatePreference_ExplicitFalseFlags_RemainValid()
    {
        Assert.IsType<OkResult>(await _controller.UpdatePreference(MessageCategory.Marketing, false, false));
        await _commPrefService.Received(1).UpdatePreferenceAsync(_userId, MessageCategory.Marketing,
            true, false, "Profile", actorUserId: _userId, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(typeof(CommunicationPreferencesController), "emailEnabled")]
    [InlineData(typeof(CommunicationPreferencesController), "alertEnabled")]
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
}
