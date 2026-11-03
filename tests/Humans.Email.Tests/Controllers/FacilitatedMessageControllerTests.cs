using Xunit;
using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NodaTime;
using NSubstitute;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// Facilitated messaging at
/// <c>/Email/SendMessage/{id}</c>: the opt-out and self guards, the Reply-To rule and the audit
/// entry. Sending as a team is Teams' (<c>TeamMessageControllerTests</c>).
/// </summary>
public sealed class FacilitatedMessageControllerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly ICommunicationPreferenceService _commPrefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ILogger<FacilitatedMessageController> _logger = Substitute.For<ILogger<FacilitatedMessageController>>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();
    private readonly FacilitatedMessageController _controller;

    public FacilitatedMessageControllerTests()
    {
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var shared = Substitute.For<IStringLocalizer<Humans.Base.SharedResource>>();
        shared[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new FacilitatedMessageController(
            _users, _emailService, _emailMessages, _commPrefs, _audit, localizer, shared, _logger);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _viewerId.ToString())], "TestAuth")),
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        var viewer = Info(_viewerId, "Viewer", "viewer@example.com");
        var target = Info(_targetId, "Target", "target@example.com");
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>()).Returns(target);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [_viewerId] = viewer, [_targetId] = target });
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(true);
        _emailMessages.FacilitatedMessage(
                Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(ci => new EmailMessage("target@example.com", "Target", ci.ArgAt<Guid?>(0), "Subject", "Body",
                "facilitated_message"));
    }

    private static UserInfo Info(Guid id, string name, string email, string language = "en") =>
        new(id, name, false, language, null, Instant.FromUnixTimeSeconds(0),
            null, null, null, null, null, null,
            null, null, email, [], [], null);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task Post_AuditFailureAfterQueueing_KeepsSuccessAndLogsCritical()
    {
        var failure = new InvalidOperationException("Audit unavailable");
        _audit.LogAsync(Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>()).ThrowsAsync(failure);

        var result = await _controller.SendMessage(
            _targetId, new SendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _controller.TempData.Should().ContainKey(Humans.Base.Constants.TempDataKeys.SuccessMessage);
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        var log = _logger.ReceivedCalls()
            .Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal))
            .GetArguments();
        log[0].Should().Be(LogLevel.Critical);
        log[3].Should().BeSameAs(failure);
        log[2]!.ToString().Should().Contain(_targetId.ToString()).And.Contain(_viewerId.ToString());
    }

    [HumansFact]
    public async Task Get_ShowsTheComposerForAnotherMember()
    {
        var result = await _controller.SendMessage(_targetId, Ct);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SendMessageViewModel>().Subject;
        model.RecipientId.Should().Be(_targetId);
        model.PageTitle.Should().Be("Email_SendMessage_Title");
        model.SenderEmail.Should().Be("viewer@example.com");
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public async Task ComposerHeading_RendersTheCurrentEncodedIdentity(string language)
    {
        using var culture = new CultureScope(language);
        var result = (ViewResult)await _controller.SendMessage(_targetId, Ct);
        const string currentName = "Renamed <script>alert(1)</script> &";
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _targetId, BurnerName = currentName }, [], [], profile: null));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FacilitatedMessageControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_users);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(EmailResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var view = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/FacilitatedMessage/SendMessage.cshtml", isMainPage: false);
        Assert.True(view.Success);
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = result.Model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, view.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await view.View!.RenderAsync(context);
        var html = writer.ToString();
        var heading = Regex.Match(html, "<h1[^>]*>(?<heading>.*?)</h1>",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["heading"].Value.Trim();
        var template = new ResourceManager(typeof(EmailResource))
            .GetString("Email_SendMessage_Heading", CultureInfo.GetCultureInfo(language));
        Assert.NotNull(template);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, template,
            $"<span>{HtmlEncoder.Default.Encode(currentName)}</span>"), heading);
        Assert.DoesNotContain("name=\"RecipientDisplayName\"", html, StringComparison.Ordinal);
    }

    [HumansFact]
    public async Task Get_OwnProfile_RedirectsToTheProfile()
    {
        var result = await _controller.SendMessage(_viewerId, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
    }

    [HumansFact]
    public async Task Get_UnknownRecipient_IsNotFound()
    {
        var result = await _controller.SendMessage(Guid.NewGuid(), Ct);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Get_RecipientOptedOut_RedirectsWithAnError()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SendMessage(_targetId, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _controller.TempData.Should().ContainKey(Humans.Base.Constants.TempDataKeys.ErrorMessage);
    }

    [HumansFact]
    public async Task Post_RecipientOptedOut_DoesNotSend()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SendMessage(_targetId, new SendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Post_OwnProfile_DoesNotSend()
    {
        var result = await _controller.SendMessage(_viewerId, new SendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task Post_InvalidModel_RedisplaysTheComposerWithoutSending()
    {
        _controller.ModelState.AddModelError("Message", "Validation_Required");

        var result = await _controller.SendMessage(_targetId, new SendMessageViewModel(), Ct);

        result.Should().BeOfType<ViewResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task Post_PersonalSend_RelaysWithoutReplyToAndAudits()
    {
        var result = await _controller.SendMessage(_targetId, new SendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _emailMessages.Received(1).FacilitatedMessage(
            _targetId, "target@example.com", "Target", "Viewer", "Hello", true, "viewer@example.com", "en");
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == null && m.UserId == _targetId), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            EmailAuditActions.FacilitatedMessageSent, "User", _targetId,
            Arg.Is<string>(d => d.Contains("contact info shared: yes", StringComparison.Ordinal)),
            _viewerId);
    }

    [HumansTheory]
    [Xunit.InlineData("es", "es")]
    [Xunit.InlineData("", "en")]
    [Xunit.InlineData(" ", "en")]
    [Xunit.InlineData("not-a-culture", "en")]
    [Xunit.InlineData("pt", "en")]
    public async Task Post_RendersInTheRecipientsSupportedLanguage(string language, string expectedCulture)
    {
        using var actorCulture = new CultureScope("fr");
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [_viewerId] = Info(_viewerId, "Viewer", "viewer@example.com"),
                [_targetId] = Info(_targetId, "Target", "target@example.com", language),
            });

        await _controller.SendMessage(_targetId, new SendMessageViewModel { Message = "Hello" }, Ct);

        _emailMessages.Received(1).FacilitatedMessage(
            _targetId, "target@example.com", "Target", "Viewer", "Hello", true, "viewer@example.com", expectedCulture);
    }

    [HumansFact]
    public void Controller_IsAuthorizedAndUsesTheEmailRoute()
    {
        typeof(FacilitatedMessageController).GetCustomAttribute<AuthorizeAttribute>(inherit: false).Should().NotBeNull();
        foreach (var method in typeof(FacilitatedMessageController).GetMethods()
                     .Where(m => string.Equals(m.Name, nameof(FacilitatedMessageController.SendMessage), StringComparison.Ordinal)))
        {
            method.GetCustomAttributes<HttpMethodAttribute>().Single().Template
                .Should().Be("Email/SendMessage/{id:guid}");
        }
        typeof(FacilitatedMessageController).GetMethods()
            .Single(m => string.Equals(m.Name, nameof(FacilitatedMessageController.SendMessage), StringComparison.Ordinal)
                && m.GetParameters().Any(p => p.ParameterType == typeof(SendMessageViewModel)))
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
    }
}
