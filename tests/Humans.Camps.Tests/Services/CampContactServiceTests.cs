using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Camps.Tests.Services;

public class CampContactServiceTests : IDisposable
{
    private readonly IEmailService _emailService;
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IMemoryCache _cache;
    private readonly CampContactService _service;

    private readonly Guid _campId = Guid.NewGuid();
    private readonly Guid _senderId = Guid.NewGuid();

    public CampContactServiceTests()
    {
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _notificationEmitter = Substitute.For<INotificationEmitter>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _service = new CampContactService(
            _emailService,
            _emailMessages,
            _auditLogService,
            _notificationEmitter,
            _users,
            _cache,
            NullLogger<CampContactService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_SuccessfulSend_ReturnsSuccess()
    {
        var result = await _service.SendFacilitatedMessageAsync(
            _campId,
            "camp@example.com",
            "Cool Camp",
            _senderId,
            "Alice",
            "alice@example.com",
            "Hello camp!",
            includeContactInfo: false,
            [Guid.NewGuid()],
            "/Barrios/cool-camp");

        result.Success.Should().BeTrue();
        result.RateLimited.Should().BeFalse();

        _emailMessages.Received(1).FacilitatedMessage(
            "camp@example.com",
            "Cool Camp",
            "Alice",
            "Hello camp!",
            false,
            "alice@example.com");

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<AuditAction>(),
            Arg.Any<string>(),
            _campId,
            Arg.Any<string>(),
            _senderId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [Xunit.InlineData("en", "New message for Camp - check your email", "View camp")]
    [Xunit.InlineData("es", "Nuevo mensaje para Camp - revisa tu correo", "Ver campamento")]
    [Xunit.InlineData("de", "Neue Nachricht für Camp - prüfe deine E-Mails", "Camp ansehen")]
    [Xunit.InlineData("it", "Nuovo messaggio per Camp - controlla la tua email", "Visualizza il camp")]
    [Xunit.InlineData("fr", "Nouveau message pour Camp - consultez vos e-mails", "Voir le camp")]
    [Xunit.InlineData("ca", "Missatge nou per a Camp - revisa el teu correu", "Mostra el camp")]
    [Xunit.InlineData("xx", "New message for Camp - check your email", "View camp")]
    public async Task Contact_notice_uses_each_lead_language(string language, string title, string actionLabel)
    {
        var leadId = Guid.NewGuid();
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [leadId] = UserInfo.Create(new User { Id = leadId, PreferredLanguage = language }, [], [], [], null, [])
            });

        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId, "Alice", "alice@example.com",
            "Hello", false, [leadId], "/Barrios/camp");

        await _notificationEmitter.Received(1).SendAsync(
            NotificationSource.FacilitatedMessageReceived, NotificationClass.Informational,
            NotificationPriority.Normal, title,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == leadId),
            null, "/Barrios/camp", actionLabel, null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Contact_notice_failed_language_lookup_falls_back_to_English()
    {
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IReadOnlyDictionary<Guid, UserInfo>>(new IOException("Lookup failed")));
        var leadId = Guid.NewGuid();

        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId, "Alice", "alice@example.com",
            "Hello", false, [leadId], "/Barrios/camp");

        await _notificationEmitter.Received(1).SendAsync(
            NotificationSource.FacilitatedMessageReceived, NotificationClass.Informational,
            NotificationPriority.Normal, "New message for Camp - check your email",
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == leadId),
            null, "/Barrios/camp", "View camp", null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Contact_notice_failed_language_group_does_not_stop_other_leads()
    {
        var spanishLead = Guid.NewGuid();
        var englishLead = Guid.NewGuid();
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [spanishLead] = UserInfo.Create(new User { Id = spanishLead, PreferredLanguage = "es" }, [], [], [], null, [])
            });
        _notificationEmitter.SendAsync(Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(spanishLead)), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Delivery failed"));

        var result = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId, "Alice", "alice@example.com",
            "Hello", false, [spanishLead, englishLead], "/Barrios/camp");

        result.Success.Should().BeTrue();
        await _notificationEmitter.Received(1).SendAsync(
            NotificationSource.FacilitatedMessageReceived, NotificationClass.Informational,
            NotificationPriority.Normal, "New message for Camp - check your email",
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == englishLead),
            null, "/Barrios/camp", "View camp", null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Contact_notice_long_name_keeps_full_copy_in_body()
    {
        var campName = string.Concat(Enumerable.Repeat("🏕️", 128));
        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", campName, _senderId, "Alice", "alice@example.com",
            "Hello", false, [Guid.NewGuid()], "/Barrios/camp");

        var call = _notificationEmitter.ReceivedCalls().Single();
        var arguments = call.GetArguments();
        ((string)arguments[3]!).EnumerateRunes().Count().Should().Be(200);
        ((string)arguments[5]!).Should().Be($"New message for {campName} - check your email");
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_RateLimited_ReturnsFalse()
    {
        // First call succeeds
        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        result1.Success.Should().BeTrue();

        // Second call within rate limit window is rejected
        var result2 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello again", false, [], "/Barrios/camp");

        result2.Success.Should().BeFalse();
        result2.RateLimited.Should().BeTrue();

        // Email only sent once
        _emailMessages.Received(1).FacilitatedMessage(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_DifferentCamp_NotRateLimited()
    {
        var otherCampId = Guid.NewGuid();

        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp1@example.com", "Camp 1", _senderId,
            "Alice", "alice@example.com", "Hello 1", false, [], "/Barrios/camp-1");

        var result2 = await _service.SendFacilitatedMessageAsync(
            otherCampId, "camp2@example.com", "Camp 2", _senderId,
            "Alice", "alice@example.com", "Hello 2", false, [], "/Barrios/camp-2");

        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_EmailFails_RollsBackRateLimit()
    {
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        var act = () => _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Rate limit should be rolled back, so next attempt should not be rate-limited
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var retryResult = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        retryResult.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_PassesMessageThroughForTheRendererToSanitize()
    {
        // Sanitization now happens once, inside EmailMessageFactory.FacilitatedMessage via
        // SanitizedMarkdownRenderer (see EmailMessageFactoryTests) — this service no longer pre-strips
        // the message, so it should hand the raw text through unchanged.
        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com",
            "Hello <script>alert('xss')</script> world",
            false,
            [],
            "/Barrios/camp");

        _emailMessages.Received(1).FacilitatedMessage(
            "camp@example.com",
            "Camp",
            "Alice",
            "Hello <script>alert('xss')</script> world",
            false,
            "alice@example.com");
    }
}
