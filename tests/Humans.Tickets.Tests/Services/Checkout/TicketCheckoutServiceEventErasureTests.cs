using AwesomeAssertions;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketCheckoutServiceEventErasureTests : CheckoutServiceFixture
{
    // Strike ends 2027-06-09; the 30-day hold ends at the start of 2027-07-10 in Madrid.
    private static readonly Instant HoldPassed = Instant.FromUtc(2027, 7, 10, 5, 0);

    [HumansFact]
    public async Task EraseHeldEventData_HoldPassed_ErasesOnceWithAudit()
    {
        _ticketing.PlatformEventRef = "nowhere2027";
        _clock.Reset(HoldPassed);
        var ct = TestContext.Current.CancellationToken;

        (await _service.EraseHeldEventDataAsync(ct)).Should().Be(1);
        (await _service.EraseHeldEventDataAsync(ct)).Should().Be(0);

        await _platform.Received(1).EraseEventPersonalDataAsync("nowhere2027", Arg.Any<CancellationToken>());
        _repo.Settings.Single().PersonalDataErasedAt.Should().Be(HoldPassed);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketEventPersonalDataErased, Arg.Any<string>(), _ticketing.Id,
            Arg.Is<string>(d => d.Contains("30 day(s)", StringComparison.Ordinal)), Arg.Any<string>());
    }

    [HumansFact]
    public async Task EraseHeldEventData_BeforeHold_LeavesEvent()
    {
        _clock.Reset(HoldPassed - Duration.FromDays(1));

        (await _service.EraseHeldEventDataAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        await _platform.DidNotReceive().EraseEventPersonalDataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseHeldEventData_ShorterHoldSetting_ErasesEarlier()
    {
        _sync.GetPostEventHoldDaysAsync(Arg.Any<CancellationToken>()).Returns(5);
        _clock.Reset(Instant.FromUtc(2027, 6, 15, 5, 0));

        (await _service.EraseHeldEventDataAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task EraseHeldEventData_PlatformFails_RetriesNextRun()
    {
        _clock.Reset(HoldPassed);
        var ct = TestContext.Current.CancellationToken;
        _platform.EraseEventPersonalDataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("down")), Task.CompletedTask);

        (await _service.EraseHeldEventDataAsync(ct)).Should().Be(0);
        _repo.Settings.Single().PersonalDataErasedAt.Should().BeNull();
        (await _service.EraseHeldEventDataAsync(ct)).Should().Be(1);
    }
}
