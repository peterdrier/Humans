using Humans.Users.Services;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Contributions;

/// <summary>
/// Each status must list exactly what its apply will fix (or <c>done</c> never arrives), and each
/// apply must carry the acting admin and be a no-op once nothing is pending.
/// </summary>
public class SectionDataMigrationsTests
{
    private readonly IEmailProblemsService _emailProblems = Substitute.For<IEmailProblemsService>();
    private readonly IAccountMergeService _merges = Substitute.For<IAccountMergeService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IServiceProvider _services;

    public SectionDataMigrationsTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_emailProblems);
        services.AddSingleton(_merges);
        _services = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DataMigration Migration(string key) =>
        ((ISectionDataMigrations)new Section()).Migrations().Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Migrations_Registered_HaveUniqueUsersKeys()
    {
        var migrations = ((ISectionDataMigrations)new Section()).Migrations().ToList();

        migrations.Select(m => m.Key).Should().BeEquivalentTo(
        [
            Section.LegacyIdentityEmailsKey,
            Section.LeftoverMergeRequestsKey,
        ]);
        migrations.Should().OnlyContain(m => string.Equals(m.Section, "Users", StringComparison.Ordinal) && m.Key.StartsWith("users.", StringComparison.Ordinal));
        migrations.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m.Title) && !string.IsNullOrWhiteSpace(m.Retires));
    }

    [HumansFact]
    public async Task LegacyEmails_Apply_PassesActor()
    {
        var legacyUser = Guid.NewGuid();
        _emailProblems.ScanAsync(Arg.Any<CancellationToken>()).Returns(new EmailProblemsReport(
            Instant.FromUtc(2026, 1, 1, 0, 0),
            [
                new EmailProblem(EmailProblemKind.LegacyIdentityEmailNotInUserEmails, legacyUser, null, null, "old@x.com"),
                new EmailProblem(EmailProblemKind.MultipleIsPrimary, Guid.NewGuid(), null, null, null),
            ]));
        _emailProblems.BackfillLegacyIdentityEmailsAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns([(legacyUser, "old@x.com")]);
        var migration = Migration(Section.LegacyIdentityEmailsKey);

        var status = await migration.Status(_services, Ct);
        status.PendingRows.Should().ContainSingle()
            .Which.Should().Equal(legacyUser.ToString(), "old@x.com");

        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
        await _emailProblems.Received(1).BackfillLegacyIdentityEmailsAsync(_actorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LeftoverMergeRequests_Dismiss_PassesActor()
    {
        var asked = new AccountMergeUserSnapshot(Guid.NewGuid());
        var holder = new AccountMergeUserSnapshot(Guid.NewGuid());
        var request = new AccountMergeRequestSnapshot(Guid.NewGuid(), "shared@x.com", asked, holder,
            Instant.FromUtc(2026, 1, 1, 0, 0));
        _merges.GetPendingRequestsAsync(Arg.Any<CancellationToken>()).Returns([request]);
        _merges.RejectAsync(request.Id, _actorId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AccountMergeResult());
        var migration = Migration(Section.LeftoverMergeRequestsKey);

        var status = await migration.Status(_services, Ct);
        status.PendingRows.Should().ContainSingle()
            .Which.Take(3).Should().Equal("shared@x.com", asked.Id.ToString(), holder.Id.ToString());

        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
        await _merges.Received(1).RejectAsync(request.Id, _actorId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
