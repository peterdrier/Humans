using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Contributions;

/// <summary>
/// Users' legacy-email data migration on <c>/Debug/DataMigrations</c>.
/// Each status must list exactly what its apply will fix (or <c>done</c> never arrives), and each
/// apply must carry the acting admin and be a no-op once nothing is pending.
/// </summary>
public class SectionDataMigrationsTests
{
    private readonly IEmailProblemsService _emailProblems = Substitute.For<IEmailProblemsService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IServiceProvider _services;

    public SectionDataMigrationsTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_emailProblems);
        _services = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DataMigration Migration(string key) =>
        new SectionDataMigrations().Migrations().Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Registers_each_migration_under_a_unique_users_key()
    {
        var migrations = new SectionDataMigrations().Migrations().ToList();

        migrations.Select(m => m.Key).Should().BeEquivalentTo(
        [
            SectionDataMigrations.LegacyIdentityEmailsKey,
        ]);
        migrations.Should().OnlyContain(m => string.Equals(m.Section, "Users", StringComparison.Ordinal) && m.Key.StartsWith("users.", StringComparison.Ordinal));
        migrations.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m.Title) && !string.IsNullOrWhiteSpace(m.Retires));
    }

    [HumansFact]
    public async Task Legacy_emails_lists_only_legacy_problems_and_applies_with_the_actor()
    {
        var legacyUser = Guid.NewGuid();
        _emailProblems.ScanAsync(Arg.Any<CancellationToken>()).Returns(new EmailProblemsReport(
            Instant.FromUtc(2026, 1, 1, 0, 0),
            [
                new EmailProblem(EmailProblemKind.LegacyIdentityEmailNotInUserEmails, legacyUser, null, null, "old@x.com", null),
                new EmailProblem(EmailProblemKind.MultipleIsPrimary, Guid.NewGuid(), null, null, null, null),
            ]));
        _emailProblems.BackfillLegacyIdentityEmailsAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns([(legacyUser, "old@x.com")]);
        var migration = Migration(SectionDataMigrations.LegacyIdentityEmailsKey);

        var status = await migration.Status(_services, Ct);
        status.PendingRows.Should().ContainSingle()
            .Which.Should().Equal(legacyUser.ToString(), "old@x.com");

        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
        await _emailProblems.Received(1).BackfillLegacyIdentityEmailsAsync(_actorId, Arg.Any<CancellationToken>());
    }
}
