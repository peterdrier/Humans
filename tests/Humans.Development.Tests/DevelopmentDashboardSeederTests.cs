using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Development.Services;
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

        var sut = new DevelopmentDashboardSeeder(
            shifts, Substitute.For<IBurnSettingsService>(), signups,
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
    }
}
