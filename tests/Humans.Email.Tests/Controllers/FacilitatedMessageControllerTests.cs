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
            _users, _emailService, _emailMessages, _commPrefs, _audit, localizer, shared);

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
            null, null, email, [], [], [], null, []);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task Get_ShowsTheComposerForAnotherMember()
    {
        var result = await _controller.SendMessage(_targetId, Ct);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SendMessageViewModel>().Subject;
        model.RecipientId.Should().Be(_targetId);
        model.RecipientDisplayName.Should().Be("Target");
        model.SenderEmail.Should().Be("viewer@example.com");
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
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
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
            AuditAction.FacilitatedMessageSent, "User", _targetId,
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
        typeof(FacilitatedMessageController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
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
