using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Humans.Users.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Email.Tests.Services;

/// <summary>
/// End-to-end guard for the Article 17 hole codex found on PR 1446: the deletion
/// job mails a confirmation to the address it has just erased, and the outbox row
/// that used to back that send re-created the address, name and rendered body the
/// cascade had removed — with a null <c>UserId</c> (the lookup no longer resolves
/// post-erasure), so neither <c>EmailOutboxService.EraseForUserAsync</c> nor the
/// retention sweep could ever reach it.
///
/// Runs the real <see cref="ProcessAccountDeletionsJob"/> over the real
/// <see cref="EmailMessageFactory"/>, <see cref="OutboxEmailService"/> and
/// <see cref="EmailOutboxRepository"/> (EF InMemory) so the whole chain — job →
/// template → send path → table — is exercised, not just the flag.
/// </summary>
public sealed class DeletionConfirmationLeavesNoOutboxRowTests : IDisposable
{
    private const string ErasedEmail = "gone@example.com";
    private const string ErasedName = "Real Name";

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    private readonly EmailDbContext _emailDb;
    private readonly IEmailTransport _transport = Substitute.For<IEmailTransport>();
    private readonly ProcessAccountDeletionsJob _job;
    private readonly Guid _userId = Guid.NewGuid();

    public DeletionConfirmationLeavesNoOutboxRowTests()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _emailDb = new EmailDbContext(options);

        var renderer = Substitute.For<IEmailRenderer>();
        renderer.RenderAccountDeleted(ErasedName, Arg.Any<string?>())
            .Returns(new EmailContent($"Goodbye {ErasedName}", $"<p>{ErasedName}, your account is gone.</p>"));

        var bodyComposer = Substitute.For<IEmailBodyComposer>();
        bodyComposer.Compose(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => ((string)ci[0], "plain-text-stub"));

        var emailService = new OutboxEmailService(
            new EmailOutboxRepository(new TestDbContextFactory<EmailDbContext>(options)),
            Substitute.For<IUserEmailService>(),
            bodyComposer,
            Substitute.For<IImmediateOutboxProcessor>(),
            _transport,
            Substitute.For<IHumansMetrics>(),
            new FakeClock(Now),
            Substitute.For<ICommunicationPreferenceService>(),
            NullLogger<OutboxEmailService>.Instance);

        var userService = Substitute.For<IUserServiceRead>();
        userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([_userId]);

        var deletionService = Substitute.For<IAccountDeletionService>();
        deletionService.AnonymizeExpiredAccountAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(ErasedEmail, ErasedName, "en"));

        _job = new ProcessAccountDeletionsJob(
            userService,
            deletionService,
            emailService,
            new EmailMessageFactory(renderer),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IHumansMetrics>(),
            NullLogger<ProcessAccountDeletionsJob>.Instance,
            new FakeClock(Now));
    }

    public void Dispose() => _emailDb.Dispose();

    [HumansFact]
    public async Task DeletionJob_LeavesNoOutboxRowCarryingTheErasedIdentity()
    {
        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await _emailDb.EmailOutboxMessages.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeletionJob_StillDeliversTheConfirmation()
    {
        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.Received(1).SendAsync(
            ErasedEmail, ErasedName, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeletionJob_UndeliverableConfirmation_DoesNotFailTheErasure()
    {
        _transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP down")));

        // The erasure has already committed by the time the courtesy mail goes out;
        // a transport failure must neither propagate nor leave a row behind.
        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        (await _emailDb.EmailOutboxMessages.AnyAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
