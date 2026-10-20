using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Contributions;

/// <summary>
/// Users' data migrations — the four retired Temp screens re-homed on <c>/Debug/DataMigrations</c>.
/// Each status must list exactly what its apply will fix (or <c>done</c> never arrives), and each
/// apply must carry the acting admin and be a no-op once nothing is pending.
/// </summary>
public class SectionDataMigrationsTests
{
    private readonly IUserManagement _users = Substitute.For<IUserManagement>();
    private readonly IUserNameSyncService _nameSync = Substitute.For<IUserNameSyncService>();
    private readonly IProfilePictureService _pictures = Substitute.For<IProfilePictureService>();
    private readonly IEmailProblemsService _emailProblems = Substitute.For<IEmailProblemsService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IServiceProvider _services;

    public SectionDataMigrationsTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_users);
        services.AddSingleton(_nameSync);
        services.AddSingleton(_pictures);
        services.AddSingleton(_emailProblems);
        _services = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DataMigration Migration(string key) =>
        new SectionDataMigrations().Migrations().Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Registers_the_four_retired_temp_screens_under_unique_users_keys()
    {
        var migrations = new SectionDataMigrations().Migrations().ToList();

        migrations.Select(m => m.Key).Should().BeEquivalentTo(
        [
            SectionDataMigrations.StubProfilesKey,
            SectionDataMigrations.NamesOntoUserKey,
            SectionDataMigrations.PicturesToFilesystemKey,
            SectionDataMigrations.LegacyIdentityEmailsKey,
        ]);
        migrations.Should().OnlyContain(m => string.Equals(m.Section, "Users", StringComparison.Ordinal) && m.Key.StartsWith("users.", StringComparison.Ordinal));
        migrations.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m.Title) && !string.IsNullOrWhiteSpace(m.Retires));
    }

    [HumansFact]
    public async Task Stub_profiles_lists_only_live_profile_less_users()
    {
        var bare = Guid.NewGuid();
        var profiled = Guid.NewGuid();
        var merged = Guid.NewGuid();
        StubUsers(
            Info(bare),
            Info(profiled, withProfile: true),
            Info(merged, mergedAt: Instant.FromUtc(2026, 2, 1, 0, 0)));

        var status = await Migration(SectionDataMigrations.StubProfilesKey).Status(_services, Ct);

        status.Pending.Should().Be(1);
        status.PendingRows.Single()[0].Should().Be(bare.ToString());
        status.Columns.Should().HaveCount(status.PendingRows.Single().Count);
    }

    [HumansFact]
    public async Task Stub_profiles_apply_ensures_each_pending_user_for_the_actor_then_finds_nothing()
    {
        var bare = Guid.NewGuid();
        StubUsers(Info(bare));
        _users.EnsureStubProfileAsync(bare, null, null, null, Arg.Any<CancellationToken>(), _actorId)
            .Returns(true);
        var migration = Migration(SectionDataMigrations.StubProfilesKey);

        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
        await _users.Received(1).EnsureStubProfileAsync(bare, null, null, null, Arg.Any<CancellationToken>(), _actorId);

        // The profile now exists: a second apply writes nothing and calls nothing.
        StubUsers(Info(bare, withProfile: true));
        _users.ClearReceivedCalls();

        (await migration.Status(_services, Ct)).Done.Should().BeTrue();
        (await migration.Apply(_services, _actorId, Ct)).Should().Be(0);
        await _users.DidNotReceiveWithAnyArgs().EnsureStubProfileAsync(default);
    }

    [HumansFact]
    public async Task Names_onto_user_lists_unsynced_rows_and_applies_with_the_actor()
    {
        var userId = Guid.NewGuid();
        _nameSync.GetUnsyncedAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UnsyncedNameRow(userId, "a@x.com", UserState.Active, "Sparkle", "Ada Lovelace", true, false),
        ]);
        _nameSync.SyncAllAsync(_actorId, Arg.Any<CancellationToken>()).Returns(1);
        var migration = Migration(SectionDataMigrations.NamesOntoUserKey);

        var status = await migration.Status(_services, Ct);
        status.PendingRows.Should().ContainSingle()
            .Which.Should().Equal(userId.ToString(), "a@x.com", "Active", "Sparkle", "Ada Lovelace", "burner");

        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
        await _nameSync.Received(1).SyncAllAsync(_actorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Pictures_apply_counts_only_rows_the_read_migrated()
    {
        var migratedProfile = Guid.NewGuid();
        var missingProfile = Guid.NewGuid();
        var at = Instant.FromUtc(2026, 1, 1, 0, 0);
        _pictures.GetProfilePictureMigrationSnapshotAsync(Arg.Any<CancellationToken>()).Returns(
            new ProfilePictureMigrationSnapshot(3, 1,
            [
                new ProfilePictureMigrationRow(migratedProfile, Guid.NewGuid(), "A", "image/png", at),
                new ProfilePictureMigrationRow(missingProfile, Guid.NewGuid(), "B", "image/jpeg", at),
            ]));
        _pictures.GetProfilePictureAsync(migratedProfile, Arg.Any<CancellationToken>())
            .Returns(([1, 2, 3], "image/png"));
        _pictures.GetProfilePictureAsync(missingProfile, Arg.Any<CancellationToken>())
            .Returns(((byte[], string)?)null);
        var migration = Migration(SectionDataMigrations.PicturesToFilesystemKey);

        (await migration.Status(_services, Ct)).Pending.Should().Be(2);
        (await migration.Apply(_services, _actorId, Ct)).Should().Be(1);
    }

    [HumansFact]
    public async Task Legacy_emails_lists_only_legacy_problems_and_applies_with_the_actor()
    {
        var legacyUser = Guid.NewGuid();
        _emailProblems.ScanAsync(Arg.Any<CancellationToken>()).Returns(new EmailProblemsReport(
            Instant.FromUtc(2026, 1, 1, 0, 0),
            [
                new EmailProblem(EmailProblemKind.LegacyIdentityEmailNotInUserEmails, legacyUser, null, null, "old@x.com", null),
                new EmailProblem(EmailProblemKind.ZeroIsGoogle, Guid.NewGuid(), null, null, null, null),
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

    private void StubUsers(params UserInfo[] infos) =>
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(infos));

    private static UserInfo Info(Guid userId, bool withProfile = false, Instant? mergedAt = null) =>
        new User
        {
            Id = userId,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            MergedAt = mergedAt,
        }.ToUserInfo(
            [],
            withProfile
                ? new Profile
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    BurnerName = "Test",
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                }
                : null);
}
