using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Development.Services;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Development.Tests;

public class DevelopmentDashboardSeederTests
{
    [HumansFact]
    public async Task Seed_CreatesNamedProfilesBeforeSelfSignups()
    {
        var now = Instant.FromUtc(2026, 9, 16, 12, 0);
        var shifts = Substitute.For<IShiftSeeding>();
        shifts.CreateRotaAsync(Arg.Any<CreateRotaInput>(), Arg.Any<IReadOnlyList<Guid>?>())
            .Returns(_ => Guid.NewGuid());
        shifts.CreateShiftAsync(Arg.Any<CreateShiftInput>())
            .Returns(_ => new ShiftMutationResult(true, "Created", Guid.NewGuid()));

        var teams = Substitute.For<ITeamSeeding>();
        teams.CreateTeamAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => new TeamInfo(
                Guid.NewGuid(), call.ArgAt<string>(0), null, Guid.NewGuid().ToString(),
                true, false, SystemTeamType.None, true, false, false, false, now, [],
                ParentTeamId: call.ArgAt<Guid?>(3)));

        var userManager = Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
        var storedStates = new Dictionary<Guid, UserState>();
        userManager.CreateAsync(Arg.Any<User>()).Returns(call =>
        {
            var user = call.Arg<User>();
            storedStates.Add(user.Id, user.State);
            return IdentityResult.Success;
        });
        userManager.UpdateAsync(Arg.Any<User>()).Returns(call =>
        {
            var user = call.Arg<User>();
            storedStates[user.Id] = user.State;
            return IdentityResult.Success;
        });

        var profiles = new Dictionary<Guid, ProfileSaveRequest>();
        var profileEditor = Substitute.For<IProfileEditorService>();
        profileEditor.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                profiles.Add(call.Arg<Guid>(), call.Arg<ProfileSaveRequest>());
                storedStates[call.Arg<Guid>()] = UserState.Active;
                return Guid.NewGuid();
            });

        var selfSignupUsers = new List<Guid>();
        var signups = Substitute.For<IShiftSignupSeeding>();
        signups.SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
            .Returns(call =>
            {
                var userId = call.ArgAt<Guid>(0);
                profiles.Should().ContainKey(userId, "the normal profile save must precede self-signup");
                storedStates[userId].Should().Be(UserState.Active);
                selfSignupUsers.Add(userId);
                return SignupResult.Ok(Guid.NewGuid());
            });
        signups.VoluntellAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));
        signups.BailAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));
        signups.RefuseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));

        var eventSettingsSeeding = Substitute.For<IEventSettingsSeeding>();
        var sut = new DevelopmentDashboardSeeder(
            shifts, Substitute.For<ISettingsService>(), eventSettingsSeeding, signups,
            Substitute.For<ITeamService>(), teams, Substitute.For<IUserEmailService>(),
            Substitute.For<IUserService>(), profileEditor, userManager, new FakeClock(now),
            NullLogger<DevelopmentDashboardSeeder>.Instance);

        var result = await sut.SeedAsync(Xunit.TestContext.Current.CancellationToken);

        selfSignupUsers.Should().NotBeEmpty();
        profiles.Should().HaveCount(result.UsersCreated);
        storedStates.Values.Should().OnlyContain(state => state == UserState.Active,
            "later writes of the original Identity entity must not undo profile-derived state");
        profiles.Values.Should().AllSatisfy(profile =>
        {
            profile.BurnerName.Should().NotBeNullOrWhiteSpace();
            profile.FirstName.Should().NotBeNullOrWhiteSpace();
            profile.LastName.Should().NotBeNullOrWhiteSpace();
        });
        await eventSettingsSeeding.Received().CreateActiveEventAsync(
            Arg.Is<EventSettingsInfo>(settings =>
                settings.BuildStartOffset <= settings.FirstCrewStartOffset),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression for nobodies-collective/Humans#1631b: <c>ResetAsync</c> must drop the
    /// Settings-owned event row too, or a second <c>SeedAsync</c> reports AlreadySeeded
    /// forever even though the Shifts/Teams/Users data is gone.
    /// </summary>
    [HumansFact]
    public async Task ResetThenSeed_SeedsAgainInsteadOfReportingAlreadySeeded()
    {
        var now = Instant.FromUtc(2026, 9, 16, 12, 0);
        var store = new FakeEventSettingsStore();

        var shifts = Substitute.For<IShiftSeeding>();
        shifts.CreateRotaAsync(Arg.Any<CreateRotaInput>(), Arg.Any<IReadOnlyList<Guid>?>())
            .Returns(_ => Guid.NewGuid());
        shifts.CreateShiftAsync(Arg.Any<CreateShiftInput>())
            .Returns(_ => new ShiftMutationResult(true, "Created", Guid.NewGuid()));
        shifts.DeleteEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);

        var teams = Substitute.For<ITeamSeeding>();
        teams.CreateTeamAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => new TeamInfo(
                Guid.NewGuid(), call.ArgAt<string>(0), null, Guid.NewGuid().ToString(),
                true, false, SystemTeamType.None, true, false, false, false, now, [],
                ParentTeamId: call.ArgAt<Guid?>(3)));

        var userManager = Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
        userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        var profileEditor = Substitute.For<IProfileEditorService>();
        profileEditor.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Guid.NewGuid());

        var signups = Substitute.For<IShiftSignupSeeding>();
        signups.SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));
        signups.VoluntellAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));
        signups.BailAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));
        signups.RefuseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(_ => SignupResult.Ok(Guid.NewGuid()));

        var sut = new DevelopmentDashboardSeeder(
            shifts, store, store, signups,
            Substitute.For<ITeamService>(), teams, Substitute.For<IUserEmailService>(),
            Substitute.For<IUserService>(), profileEditor, userManager, new FakeClock(now),
            NullLogger<DevelopmentDashboardSeeder>.Instance);

        var firstSeed = await sut.SeedAsync(Xunit.TestContext.Current.CancellationToken);
        firstSeed.AlreadySeeded.Should().BeFalse();

        await sut.ResetAsync(Xunit.TestContext.Current.CancellationToken);

        var secondSeed = await sut.SeedAsync(Xunit.TestContext.Current.CancellationToken);
        secondSeed.AlreadySeeded.Should().BeFalse("reset must clear the Settings-owned event row too");
    }

    /// <summary>Minimal stateful fake standing in for Settings' <c>Service</c> — one
    /// instance backing both interfaces, as the real registration does.</summary>
    private sealed class FakeEventSettingsStore : ISettingsService, IEventSettingsSeeding
    {
        private readonly Dictionary<Guid, EventSettingsInfo> _events = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<EventSettingsInfo?> GetActiveEventSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.Values.FirstOrDefault(e => e.Status == EventSettingsStatus.Active));

        public Task<EventSettingsInfo?> GetEventSettingsByIdAsync(
            Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.GetValueOrDefault(id));

        public Task CreateActiveEventAsync(
            EventSettingsInfo settings, CancellationToken cancellationToken = default)
        {
            _events[settings.Id] = settings with { Status = EventSettingsStatus.Active };
            return Task.CompletedTask;
        }

        public Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.Remove(id) ? 1 : 0);
    }
}
