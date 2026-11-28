using AwesomeAssertions;
using Humans.Users.Contracts;
using NodaTime;
using Xunit;
using Humans.Users.Services;

namespace Humans.Users.Tests.Services.Profiles;

public class AdminHumanListAssemblerTests
{
    private static readonly IReadOnlyDictionary<Guid, string> NoEmails = new Dictionary<Guid, string>();

    private static UserInfo Build(
        UserState state = UserState.Active,
        string displayName = "Burner",
        string email = "user@example.com")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = state,
        };

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
        };

        return UserInfoFactory.Create(user, [], [], profile, [], [], []);
    }

    private static UserState StateOf(UserInfo u) =>
        AdminHumanListAssembler.Assemble([u], NoEmails, null, statusFilter: null)
            .Single().State;

    [HumansTheory]
    [InlineData(UserState.Bare)]
    [InlineData(UserState.Active)]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.Rejected)]
    [InlineData(UserState.DeletePending)]
    [InlineData(UserState.Merged)]
    [InlineData(UserState.Deleted)]
    public void Assemble_Row_TakesStateFromUser(UserState state)
        => StateOf(Build(state)).Should().Be(state);

    [HumansTheory]
    [InlineData("bare", UserState.Bare)]
    [InlineData("active", UserState.Active)]
    [InlineData("suspended", UserState.Suspended)]
    [InlineData("suspended", UserState.AdminSuspended)]
    [InlineData("adminsuspended", UserState.AdminSuspended)]
    [InlineData("rejected", UserState.Rejected)]
    [InlineData("deleting", UserState.DeletePending)]
    [InlineData("deletepending", UserState.DeletePending)]
    [InlineData("merged", UserState.Merged)]
    [InlineData("deleted", UserState.Deleted)]
    public void Assemble_StatusFilter_MatchesUserState(string filter, UserState state)
    {
        var keep = Build(state);
        var drop = Build(UserState.Active == state ? UserState.Bare : UserState.Active);

        var rows = AdminHumanListAssembler.Assemble(
            [keep, drop], NoEmails, searchUserIds: null, statusFilter: filter);

        rows.Should().ContainSingle()
            .Which.UserId.Should().Be(keep.Id);
    }

    [HumansFact]
    public void Assemble_NullFilter_ReturnsAllCandidates()
    {
        var users = new[] { Build(), Build(UserState.Bare), Build(UserState.Rejected) };

        var rows = AdminHumanListAssembler.Assemble(users, NoEmails, searchUserIds: null, statusFilter: null);

        rows.Should().HaveCount(3);
    }

    [HumansFact]
    public void Assemble_SearchUserIds_FilterBeforeStatus()
    {
        var keep = Build();
        var drop = Build();

        var rows = AdminHumanListAssembler.Assemble(
            [keep, drop], NoEmails, searchUserIds: new HashSet<Guid> { keep.Id }, statusFilter: null);

        rows.Should().ContainSingle().Which.UserId.Should().Be(keep.Id);
    }
}
