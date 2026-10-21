using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;

namespace Humans.Shifts.Tests.Services;

public sealed class ToggleDaySignupOutcomeTests
{
    [HumansFact]
    public void ActiveSignupCount_CountsOnlyPendingAndConfirmed()
    {
        var signups = new[]
        {
            SignupStatus.Pending, SignupStatus.Confirmed, SignupStatus.Bailed,
            SignupStatus.Refused, SignupStatus.Cancelled, SignupStatus.NoShow
        }.Select(status => new ShiftSignup { Id = Guid.NewGuid(), Status = status }).ToList();

        var outcome = new ToggleDaySignupOutcome(SignupResult.Ok(Guid.NewGuid()), true, false, signups);

        outcome.ActiveSignupCount.Should().Be(2);
    }
}
