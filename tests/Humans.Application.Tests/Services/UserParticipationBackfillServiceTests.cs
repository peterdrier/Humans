using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class UserParticipationBackfillServiceTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private List<(Guid UserId, ParticipationStatus Status)>? _captured;

    private UserParticipationBackfillService CreateService()
    {
        _users.BackfillParticipationsAsync(
                Arg.Any<int>(),
                Arg.Do<List<(Guid UserId, ParticipationStatus Status)>>(e => _captured = e),
                Arg.Any<CancellationToken>())
            .Returns(ci => ((List<(Guid UserId, ParticipationStatus Status)>)ci[1]).Count);

        return new UserParticipationBackfillService(
            _users, _shifts, new FakeClock(Instant.FromUtc(2026, 6, 11, 0, 0)));
    }

    [HumansFact]
    public async Task SkipsHeaderAndUnparseableRows_PassesValidEntries()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var csv = $"UserId,Status\n{a},Ticketed\nnot-a-guid,Ticketed\n{b},notastatus\n{b},NotAttending\n";

        var result = await CreateService().BackfillFromCsvAsync(2026, csv);

        result.Succeeded.Should().BeTrue();
        _captured.Should().Equal((a, ParticipationStatus.Ticketed), (b, ParticipationStatus.NotAttending));
    }

    [HumansFact]
    public async Task ParsesQuotedAndSemicolonDelimitedRows()
    {
        var a = Guid.NewGuid();
        var csv = $"\"{a}\";\"Ticketed\"\n";

        var result = await CreateService().BackfillFromCsvAsync(2026, csv);

        result.Succeeded.Should().BeTrue();
        _captured.Should().Equal((a, ParticipationStatus.Ticketed));
    }

    [HumansFact]
    public async Task NoValidEntries_FailsWithoutCallingUserService()
    {
        var result = await CreateService().BackfillFromCsvAsync(2026, "garbage\nmore,garbage\n");

        result.Succeeded.Should().BeFalse();
        await _users.DidNotReceiveWithAnyArgs().BackfillParticipationsAsync(0, null!, CancellationToken.None);
    }
}
