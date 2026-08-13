using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Email.Contracts;
using Humans.Application.Interfaces.Profiles;
using Humans.Email.Services;
using Humans.Domain.Enums;
using Humans.Email.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Diagnostics.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Email.Tests.Services;

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
public sealed class OutboxEmailServiceTests : IDisposable
{
    // Two members of Humans.Application.Tests' ServiceTestHarness, owned here rather than
    // inherited: the harness is built around an in-memory UsersDbContext and sharing it
    // would grant a section test project InternalsVisibleTo on it (design §15 step 8).
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));

    private static DbContextOptions<TContext> NewSectionDbOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    public void Dispose() => _emailDb.Dispose();

    private readonly OutboxEmailService _service;
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IImmediateOutboxProcessor _immediate = Substitute.For<IImmediateOutboxProcessor>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailBodyComposer _bodyComposer = Substitute.For<IEmailBodyComposer>();

    /// <summary>
    /// <c>email_outbox_messages</c> moved to <see cref="EmailDbContext"/> with
    /// the Email peel (nobodies-collective/Humans#858).
    /// </summary>
    private readonly EmailDbContext _emailDb;

    public OutboxEmailServiceTests()
    {
        // Default composer stub: returns the input HTML plus a stub plain text.
        _bodyComposer.Compose(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => ((string)ci[0], "plain-text-stub"));

        var emailDbOptions = NewSectionDbOptions<EmailDbContext>();
        _emailDb = new EmailDbContext(emailDbOptions);
        var repo = new EmailOutboxRepository(new TestDbContextFactory<EmailDbContext>(emailDbOptions));

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
        await _service.SendAsync(Message(subject: "Access Suspended", html: "<p>Hello Alice</p>"), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
        await _service.SendAsync(Message(template: "access_suspended"), Xunit.TestContext.Current.CancellationToken);
        _metrics.Received(1).RecordEmailQueued("access_suspended");
    }

    [HumansFact]
    public async Task SendAsync_TriggerImmediate_RunsImmediateProcessor()
    {
        await _service.SendAsync(Message(template: "email_verification", triggerImmediate: true), Xunit.TestContext.Current.CancellationToken);
        _immediate.Received(1).TriggerImmediate();
    }

    [HumansFact]
    public async Task SendAsync_WithoutTriggerImmediate_DoesNotRunImmediateProcessor()
    {
        await _service.SendAsync(Message(triggerImmediate: false), Xunit.TestContext.Current.CancellationToken);
        _immediate.DidNotReceive().TriggerImmediate();
    }

    [HumansFact]
    public async Task SendAsync_PersistsReplyTo()
    {
        await _service.SendAsync(Message(
            template: "facilitated_message",
            category: MessageCategory.FacilitatedMessages,
            replyTo: "dave@example.com"), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        msg.ReplyTo.Should().Be("dave@example.com");
    }

    [HumansFact]
    public async Task SendAsync_NullCategory_NeverSuppressesAndStampsNoUnsubscribe()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);

        await _service.SendAsync(Message(category: null), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
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

        await _service.SendAsync(Message(template: "signup_rejected", category: MessageCategory.System), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
            template: "added_to_team", category: MessageCategory.TeamUpdates), Xunit.TestContext.Current.CancellationToken);

        (await _emailDb.EmailOutboxMessages.ToListAsync(Xunit.TestContext.Current.CancellationToken)).Should()
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
            template: "application_approved", category: MessageCategory.Governance), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        msg.UserId.Should().Be(userId);
        msg.ExtraHeaders.Should().NotBeNull("List-Unsubscribe headers must be stamped for opt-outable mail");
        _bodyComposer.Received().Compose(Arg.Any<string>(), "https://example.com/Unsubscribe/abc");
    }

    [HumansFact]
    public async Task SendAsync_ExplicitUserId_UsedDirectlyWithoutAddressLookup()
    {
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        _commPrefService.GenerateUnsubscribeHeaders(userId, MessageCategory.Governance)
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal) { ["List-Unsubscribe"] = "<mailto:x>" });
        _commPrefService.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.Governance)
            .Returns("https://example.com/unsub");

        await _service.SendAsync(Message(
            recipient: "zoe@example.com", name: "Zoe",
            template: "application_approved", category: MessageCategory.Governance,
            replyTo: "reply@example.com", userId: userId, campaignGrantId: grantId), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        msg.UserId.Should().Be(userId);
        msg.CampaignGrantId.Should().Be(grantId);
        msg.ReplyTo.Should().Be("reply@example.com");
        msg.ExtraHeaders.Should().NotBeNull();
        await _userEmailService.DidNotReceive()
            .GetUserIdByVerifiedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendAsync_CampaignCodesCategory_NeverSuppressesAndStampsNoUnsubscribe()
    {
        // CampaignCodes is always-on (confirmed intended, nobodies-collective/Humans#1032):
        // the opt-out it would advertise can never take effect, so the transport must not
        // advertise one — no List-Unsubscribe headers, no footer link, and it never even
        // asks CommunicationPreferenceService about opt-out status.
        var userId = Guid.NewGuid();

        await _service.SendAsync(Message(
            template: "campaign_code", category: MessageCategory.CampaignCodes, userId: userId), Xunit.TestContext.Current.CancellationToken);

        var msg = await _emailDb.EmailOutboxMessages.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        msg.ExtraHeaders.Should().BeNull("campaign codes are always-on; there is no opt-out to advertise");
        await _commPrefService.DidNotReceive()
            .IsOptedOutAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>());
        _commPrefService.DidNotReceive().GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>());
        _bodyComposer.Received().Compose(Arg.Any<string>(), null);
    }
}
