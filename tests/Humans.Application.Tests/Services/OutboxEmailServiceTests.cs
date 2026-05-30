using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Email;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Transport-level tests for the Application-layer <see cref="OutboxEmailService"/>:
/// the single <see cref="IEmailService.SendAsync"/> path. Per-type policy stamping
/// (template / category / reply-to / immediate) is covered by
/// <see cref="EmailMessageFactoryTests"/>; these tests exercise the shared
/// transport — opt-out suppression, unsubscribe headers, body composition,
/// immediate-drain, and user-id resolution — over a real
/// <see cref="EmailOutboxRepository"/> (EF InMemory) with NSubstitute fakes for the
/// cross-section and Infrastructure dependencies.
/// </summary>
public sealed class OutboxEmailServiceTests : ServiceTestHarness
{
    private readonly OutboxEmailService _service;
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IImmediateOutboxProcessor _immediate = Substitute.For<IImmediateOutboxProcessor>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailBodyComposer _bodyComposer = Substitute.For<IEmailBodyComposer>();

    public OutboxEmailServiceTests()
    {
        // Default composer stub: returns the input HTML plus a stub plain text.
        _bodyComposer.Compose(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => ((string)ci[0], "plain-text-stub"));

        var repo = new EmailOutboxRepository(DbFactory);

        _service = new OutboxEmailService(
            repo,
            _userEmailService,
            _bodyComposer,
            _immediate,
            _metrics,
            Clock,
            _commPrefService,
            NullLogger<OutboxEmailService>.Instance);
    }

    private static EmailMessage Message(
        string recipient = "alice@example.com",
        string? name = "Alice",
        string subject = "Subject",
        string html = "<p>Body</p>",
        string template = "access_suspended",
        MessageCategory? category = null,
        string? replyTo = null,
        bool triggerImmediate = false,
        Guid? userId = null,
        Guid? campaignGrantId = null) =>
        new(recipient, name, subject, html, template, category, replyTo, triggerImmediate, userId, campaignGrantId);

    [HumansFact]
    public async Task SendAsync_CreatesOutboxRowWithCorrectFields()
    {
        await _service.SendAsync(Message(subject: "Access Suspended", html: "<p>Hello Alice</p>"));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.RecipientEmail.Should().Be("alice@example.com");
        msg.RecipientName.Should().Be("Alice");
        msg.Subject.Should().Be("Access Suspended");
        msg.HtmlBody.Should().Contain("<p>Hello Alice</p>");
        msg.PlainTextBody.Should().Be("plain-text-stub");
        msg.TemplateName.Should().Be("access_suspended");
        msg.Status.Should().Be(EmailOutboxStatus.Queued);
        msg.CreatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SendAsync_RecordsEmailQueuedMetricKeyedOnTemplate()
    {
        await _service.SendAsync(Message(template: "access_suspended"));
        _metrics.Received(1).RecordEmailQueued("access_suspended");
    }

    [HumansFact]
    public async Task SendAsync_TriggerImmediate_RunsImmediateProcessor()
    {
        await _service.SendAsync(Message(template: "email_verification", triggerImmediate: true));
        _immediate.Received(1).TriggerImmediate();
    }

    [HumansFact]
    public async Task SendAsync_WithoutTriggerImmediate_DoesNotRunImmediateProcessor()
    {
        await _service.SendAsync(Message(triggerImmediate: false));
        _immediate.DidNotReceive().TriggerImmediate();
    }

    [HumansFact]
    public async Task SendAsync_PersistsReplyTo()
    {
        await _service.SendAsync(Message(
            template: "facilitated_message",
            category: MessageCategory.FacilitatedMessages,
            replyTo: "dave@example.com"));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.ReplyTo.Should().Be("dave@example.com");
    }

    [HumansFact]
    public async Task SendAsync_NullCategory_NeverSuppressesAndStampsNoUnsubscribe()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);

        await _service.SendAsync(Message(category: null));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.ExtraHeaders.Should().BeNull("always-send mail carries no List-Unsubscribe headers");
        await _commPrefService.DidNotReceive()
            .IsOptedOutAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>());
        _commPrefService.DidNotReceive().GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>());
    }

    [HumansFact]
    public async Task SendAsync_SystemCategory_NeverSuppressesAndStampsNoUnsubscribe()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);

        await _service.SendAsync(Message(template: "signup_rejected", category: MessageCategory.System));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.ExtraHeaders.Should().BeNull();
        await _commPrefService.DidNotReceive()
            .IsOptedOutAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendAsync_WhenUserOptedOutOfCategory_DoesNotCreateOutboxRow()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("charlie@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);
        _commPrefService.IsOptedOutAsync(userId, MessageCategory.TeamUpdates, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.SendAsync(Message(
            recipient: "charlie@example.com", name: "Charlie",
            template: "added_to_team", category: MessageCategory.TeamUpdates));

        (await Db.EmailOutboxMessages.ToListAsync()).Should()
            .BeEmpty("the email is suppressed because the user opted out of TeamUpdates");
    }

    [HumansFact]
    public async Task SendAsync_WhenOptedIn_StampsUnsubscribeHeadersAndUrl()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("grace@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);
        _commPrefService.IsOptedOutAsync(userId, MessageCategory.Governance, Arg.Any<CancellationToken>())
            .Returns(false);
        _commPrefService.GenerateUnsubscribeHeaders(userId, MessageCategory.Governance)
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["List-Unsubscribe"] = "<https://example.com/u>",
            });
        _commPrefService.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.Governance)
            .Returns("https://example.com/Unsubscribe/abc");

        await _service.SendAsync(Message(
            recipient: "grace@example.com", name: "Grace",
            template: "application_approved", category: MessageCategory.Governance));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.UserId.Should().Be(userId);
        msg.ExtraHeaders.Should().NotBeNull("List-Unsubscribe headers must be stamped for opt-outable mail");
        _bodyComposer.Received().Compose(Arg.Any<string>(), "https://example.com/Unsubscribe/abc");
    }

    [HumansFact]
    public async Task SendAsync_ExplicitUserId_UsedDirectlyWithoutAddressLookup()
    {
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        _commPrefService.GenerateUnsubscribeHeaders(userId, MessageCategory.CampaignCodes)
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal) { ["List-Unsubscribe"] = "<mailto:x>" });
        _commPrefService.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.CampaignCodes)
            .Returns("https://example.com/unsub");

        await _service.SendAsync(Message(
            recipient: "zoe@example.com", name: "Zoe",
            template: "campaign_code", category: MessageCategory.CampaignCodes,
            replyTo: "reply@example.com", userId: userId, campaignGrantId: grantId));

        var msg = await Db.EmailOutboxMessages.SingleAsync();
        msg.UserId.Should().Be(userId);
        msg.CampaignGrantId.Should().Be(grantId);
        msg.ReplyTo.Should().Be("reply@example.com");
        msg.ExtraHeaders.Should().NotBeNull();
        await _userEmailService.DidNotReceive()
            .GetUserIdByVerifiedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendAsync_CampaignCode_AlwaysOn_IsNeverSuppressed()
    {
        // CampaignCodes is always-on, so CommunicationPreferenceService reports
        // not-opted-out; the campaign mail enqueues regardless (preserves the old
        // inline-enqueue bypass behaviour now that it routes through SendAsync).
        var userId = Guid.NewGuid();
        _commPrefService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.SendAsync(Message(
            template: "campaign_code", category: MessageCategory.CampaignCodes, userId: userId));

        (await Db.EmailOutboxMessages.ToListAsync()).Should().HaveCount(1);
    }
}
