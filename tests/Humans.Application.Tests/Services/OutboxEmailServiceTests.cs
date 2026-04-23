using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Email;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Xunit;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Service-level tests for the Application-layer <see cref="OutboxEmailService"/>
/// (Email §15 migration, issue #548). Uses a real
/// <see cref="EmailOutboxRepository"/> backed by the EF InMemory provider so the
/// enqueue path is exercised end-to-end; cross-section abstractions
/// (<see cref="IUserEmailService"/>, <see cref="ICommunicationPreferenceService"/>)
/// and Infrastructure connectors (<see cref="IEmailBodyComposer"/>,
/// <see cref="IImmediateOutboxProcessor"/>) are NSubstitute fakes so the Application
/// service stays free of Infrastructure dependencies.
/// </summary>
public sealed class OutboxEmailServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly OutboxEmailService _service;
    private readonly IEmailRenderer _renderer = Substitute.For<IEmailRenderer>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IImmediateOutboxProcessor _immediate = Substitute.For<IImmediateOutboxProcessor>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailBodyComposer _bodyComposer = Substitute.For<IEmailBodyComposer>();

    public OutboxEmailServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        // Default composer stub: returns the input HTML plus a stub plain text.
        _bodyComposer.Compose(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => ((string)ci[0], "plain-text-stub"));

        var factory = new TestDbContextFactory(options);
        var repo = new EmailOutboxRepository(factory);

        _service = new OutboxEmailService(
            repo,
            _userEmailService,
            _renderer,
            _bodyComposer,
            _immediate,
            _metrics,
            _clock,
            _commPrefService,
            NullLogger<OutboxEmailService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_CreatesOutboxRowWithCorrectFields()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello Alice</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);

        var msg = messages[0];
        msg.RecipientEmail.Should().Be("alice@example.com");
        msg.RecipientName.Should().Be("Alice");
        msg.Subject.Should().Be("Welcome!");
        msg.HtmlBody.Should().Contain("<p>Hello Alice</p>");
        msg.PlainTextBody.Should().Be("plain-text-stub");
        msg.TemplateName.Should().Be("welcome");
        msg.Status.Should().Be(EmailOutboxStatus.Queued);
        msg.CreatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_RecordsEmailQueuedMetric()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        _metrics.Received(1).RecordEmailQueued("welcome");
    }

    [Fact]
    public async Task SendEmailVerificationAsync_CreatesOutboxRowAndTriggersImmediate()
    {
        _renderer.RenderEmailVerification("Bob", "bob@example.com", "https://verify", false, "en")
            .Returns(new EmailContent("Verify Email", "<p>Click to verify</p>"));

        await _service.SendEmailVerificationAsync("bob@example.com", "Bob", "https://verify", culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);

        var msg = messages[0];
        msg.TemplateName.Should().Be("email_verification");
        msg.Status.Should().Be(EmailOutboxStatus.Queued);

        // Verify immediate processor was triggered
        _immediate.Received(1).TriggerImmediate();
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_SetsReplyToOnOutboxMessage()
    {
        _renderer.RenderFacilitatedMessage("Charlie", "Dave", "Hey!", true, "dave@example.com", "en")
            .Returns(new EmailContent("Message from Dave", "<p>Hey!</p>"));

        await _service.SendFacilitatedMessageAsync(
            "charlie@example.com", "Charlie", "Dave", "Hey!",
            includeContactInfo: true, senderEmail: "dave@example.com", culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].ReplyTo.Should().Be("dave@example.com");
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_NoContactInfo_ReplyToIsNull()
    {
        _renderer.RenderFacilitatedMessage("Charlie", "Dave", "Hey!", false, null, "en")
            .Returns(new EmailContent("Message from Dave", "<p>Hey!</p>"));

        await _service.SendFacilitatedMessageAsync(
            "charlie@example.com", "Charlie", "Dave", "Hey!",
            includeContactInfo: false, senderEmail: null, culture: "en");

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].ReplyTo.Should().BeNull();
    }

    [Fact]
    public async Task SendApplicationApprovedAsync_CreatesOutboxRowWithCorrectTemplateName()
    {
        _renderer.RenderApplicationApproved("Eve", MembershipTier.Colaborador, "en")
            .Returns(new EmailContent("Approved!", "<p>Congrats</p>"));

        await _service.SendApplicationApprovedAsync(
            "eve@example.com", "Eve", MembershipTier.Colaborador, "en");

        var msg = await _dbContext.EmailOutboxMessages.SingleAsync();
        msg.TemplateName.Should().Be("application_approved");
        msg.RecipientEmail.Should().Be("eve@example.com");
        _metrics.Received(1).RecordEmailQueued("application_approved");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_DoesNotTriggerImmediate()
    {
        _renderer.RenderWelcome("Alice", "en")
            .Returns(new EmailContent("Welcome!", "<p>Hello</p>"));

        await _service.SendWelcomeEmailAsync("alice@example.com", "Alice", "en");

        // Welcome email should NOT trigger immediate processing
        _immediate.DidNotReceive().TriggerImmediate();
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserOptedOutOfCategory_DoesNotCreateOutboxRow()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("charlie@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);

        _commPrefService.IsOptedOutAsync(userId, MessageCategory.TeamUpdates, Arg.Any<CancellationToken>())
            .Returns(true);
        _commPrefService.GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        _commPrefService.GenerateBrowserUnsubscribeUrl(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns("https://example.com/unsubscribe/token");

        _renderer.RenderAddedToTeam(
                "Charlie", "Alpha Team", "alpha", Arg.Any<System.Collections.Generic.List<(string Name, string? Url)>>(), null)
            .Returns(new EmailContent("Added to Alpha Team", "<p>You joined Alpha Team</p>"));

        await _service.SendAddedToTeamAsync(
            "charlie@example.com", "Charlie", "Alpha Team", "alpha",
            Array.Empty<(string Name, string? Url)>());

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().BeEmpty("the email should have been suppressed because the user opted out of TeamUpdates");
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserOptedInToCategory_CreatesOutboxRow()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByVerifiedEmailAsync("dana@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);

        _commPrefService.IsOptedOutAsync(userId, MessageCategory.TeamUpdates, Arg.Any<CancellationToken>())
            .Returns(false);
        _commPrefService.GenerateUnsubscribeHeaders(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        _commPrefService.GenerateBrowserUnsubscribeUrl(Arg.Any<Guid>(), Arg.Any<MessageCategory>())
            .Returns("https://example.com/unsubscribe/token");

        _renderer.RenderAddedToTeam(
                "Dana", "Beta Team", "beta", Arg.Any<System.Collections.Generic.List<(string Name, string? Url)>>(), null)
            .Returns(new EmailContent("Added to Beta Team", "<p>You joined Beta Team</p>"));

        await _service.SendAddedToTeamAsync(
            "dana@example.com", "Dana", "Beta Team", "beta",
            Array.Empty<(string Name, string? Url)>());

        var messages = await _dbContext.EmailOutboxMessages.ToListAsync();
        messages.Should().HaveCount(1, "opted-in user should receive the email");
        messages[0].RecipientEmail.Should().Be("dana@example.com");
        messages[0].TemplateName.Should().Be("added_to_team");
        messages[0].UserId.Should().Be(userId);
    }

    [Fact]
    public async Task SendCampaignCodeAsync_UsesRendererAndStampsFullRow()
    {
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();

        _renderer.RenderCampaignCode("Subject {{Name}}", "Hello {{Name}}! Your code is {{Code}}.", "ABC123", "Zoe")
            .Returns(new EmailContent("Subject Zoe", "<p>Hello Zoe! Your code is ABC123.</p>"));

        _commPrefService.GenerateUnsubscribeHeaders(userId, MessageCategory.CampaignCodes)
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal) { ["List-Unsubscribe"] = "<mailto:x>" });
        _commPrefService.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.CampaignCodes)
            .Returns("https://example.com/unsub");

        await _service.SendCampaignCodeAsync(new CampaignCodeEmailRequest(
            UserId: userId,
            CampaignGrantId: grantId,
            RecipientEmail: "zoe@example.com",
            RecipientName: "Zoe",
            Subject: "Subject {{Name}}",
            MarkdownBody: "Hello {{Name}}! Your code is {{Code}}.",
            Code: "ABC123",
            ReplyTo: "reply@example.com"));

        var msg = await _dbContext.EmailOutboxMessages.SingleAsync();
        msg.RecipientEmail.Should().Be("zoe@example.com");
        msg.Subject.Should().Be("Subject Zoe");
        msg.HtmlBody.Should().Contain("Hello Zoe! Your code is ABC123.");
        msg.TemplateName.Should().Be("campaign_code");
        msg.UserId.Should().Be(userId);
        msg.CampaignGrantId.Should().Be(grantId);
        msg.ReplyTo.Should().Be("reply@example.com");
        msg.ExtraHeaders.Should().NotBeNull();
        _metrics.Received(1).RecordEmailQueued("campaign_code");
    }
}
