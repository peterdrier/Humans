using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Tickets.Tests.Services;

public sealed class UserParticipationBackfillServiceTests
{
    private readonly IEventParticipationManagement _users = Substitute.For<IEventParticipationManagement>();
    private readonly ISettingsService _shifts = Substitute.For<ISettingsService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private IReadOnlyList<(Guid UserId, ParticipationStatus Status)>? _captured;

    private UserParticipationBackfillService CreateService()
    {
        _users.BackfillParticipationsAsync(
                Arg.Any<int>(),
                Arg.Do<IReadOnlyList<(Guid UserId, ParticipationStatus Status)>>(e => _captured = e),
                _actorId,
                Arg.Any<CancellationToken>())
            .Returns(ci => ((IReadOnlyList<(Guid UserId, ParticipationStatus Status)>)ci[1]).Count);

        return new UserParticipationBackfillService(
            _users, _shifts, new FakeClock(Instant.FromUtc(2026, 6, 11, 0, 0)));
    }

    [HumansFact]
    public async Task Preview_ShowsParsedRows_WithoutWriting()
    {
        var userId = Guid.NewGuid();
        var result = await CreateService().BackfillFromCsvAsync(2026,
            $"UserId,Status\n{userId},Ticketed\ninvalid,Attended\n{userId},99\n",
            _actorId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Count.Should().Be(1);
        result.PreviewCsv.Should().Be($"UserId,Status\n{userId},Ticketed");
        await _users.DidNotReceiveWithAnyArgs().BackfillParticipationsAsync(0, null!, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DefaultYear_RequestAborted_CancelsActiveEventRead()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _shifts.GetActiveEventSettingsAsync(aborted.Token)
            .Returns(Task.FromException<EventSettingsInfo?>(new OperationCanceledException(aborted.Token)));

        var act = () => CreateService().GetDefaultYearAsync(aborted.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task SkipsHeaderAndUnparseableRows_PassesValidEntries()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var csv = $"UserId,Status\n{a},Ticketed\nnot-a-guid,Ticketed\n{b},notastatus\n{a},99\n{b},-1\n{b},NotAttending\n";

        var result = await CreateService().BackfillFromCsvAsync(2026, csv, _actorId, Xunit.TestContext.Current.CancellationToken, apply: true);

        result.Succeeded.Should().BeTrue();
        _captured.Should().Equal((a, ParticipationStatus.Ticketed), (b, ParticipationStatus.NotAttending));
    }

    [HumansFact]
    public async Task ParsesQuotedAndSemicolonDelimitedRows()
    {
        var a = Guid.NewGuid();
        var csv = $"\"{a}\";\"Ticketed\"\n";

        var result = await CreateService().BackfillFromCsvAsync(2026, csv, _actorId, Xunit.TestContext.Current.CancellationToken, apply: true);

        result.Succeeded.Should().BeTrue();
        _captured.Should().Equal((a, ParticipationStatus.Ticketed));
    }

    [HumansTheory]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(99)]
    public async Task UndefinedNumericStatus_FailsWithoutCallingUserService(int status)
    {
        var result = await CreateService().BackfillFromCsvAsync(
            2026, $"{Guid.NewGuid()},{status}\n", _actorId, Xunit.TestContext.Current.CancellationToken, apply: true);

        result.Succeeded.Should().BeFalse();
        await _users.DidNotReceiveWithAnyArgs().BackfillParticipationsAsync(0, null!, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NoValidEntries_FailsWithoutCallingUserService()
    {
        var result = await CreateService().BackfillFromCsvAsync(2026, "garbage\nmore,garbage\n", _actorId, Xunit.TestContext.Current.CancellationToken, apply: true);

        result.Succeeded.Should().BeFalse();
        await _users.DidNotReceiveWithAnyArgs().BackfillParticipationsAsync(0, null!, default, Arg.Any<CancellationToken>());
    }
}
