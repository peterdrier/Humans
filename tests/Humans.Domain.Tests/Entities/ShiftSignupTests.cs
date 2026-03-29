using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class ShiftSignupTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 6, 12, 0);
    private static readonly FakeClock Clock = new(Now);

    private static ShiftSignup CreatePendingSignup() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        Status = SignupStatus.Pending,
        CreatedAt = Now,
        UpdatedAt = Now
    };

    private static ShiftSignup CreateConfirmedSignup() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        Status = SignupStatus.Confirmed,
        CreatedAt = Now,
        UpdatedAt = Now
    };

    [Fact]
    public void Confirm_FromPending_SetsConfirmedAndReviewer()
    {
        var signup = CreatePendingSignup();
        var reviewerId = Guid.NewGuid();

        signup.Confirm(reviewerId, Clock);

        signup.Status.Should().Be(SignupStatus.Confirmed);
        signup.ReviewedByUserId.Should().Be(reviewerId);
        signup.ReviewedAt.Should().Be(Now);
        signup.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Confirm_FromConfirmed_Throws()
    {
        var signup = CreateConfirmedSignup();

        var act = () => signup.Confirm(Guid.NewGuid(), Clock);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Refuse_FromPending_SetsRefusedAndReviewer()
    {
        var signup = CreatePendingSignup();
        var reviewerId = Guid.NewGuid();

        signup.Refuse(reviewerId, Clock, "Not enough experience");

        signup.Status.Should().Be(SignupStatus.Refused);
        signup.ReviewedByUserId.Should().Be(reviewerId);
        signup.ReviewedAt.Should().Be(Now);
        signup.StatusReason.Should().Be("Not enough experience");
    }

    [Fact]
    public void Refuse_FromConfirmed_Throws()
    {
        var signup = CreateConfirmedSignup();

        var act = () => signup.Refuse(Guid.NewGuid(), Clock, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Bail_FromConfirmed_SetsBailed()
    {
        var signup = CreateConfirmedSignup();
        var reviewerId = Guid.NewGuid();

        signup.Bail(reviewerId, Clock, "Schedule conflict");

        signup.Status.Should().Be(SignupStatus.Bailed);
        signup.ReviewedByUserId.Should().Be(reviewerId);
        signup.StatusReason.Should().Be("Schedule conflict");
    }

    [Fact]
    public void Bail_FromPending_SetsBailed()
    {
        var signup = CreatePendingSignup();

        signup.Bail(signup.UserId, Clock, "Changed my mind");

        signup.Status.Should().Be(SignupStatus.Bailed);
        signup.StatusReason.Should().Be("Changed my mind");
    }

    [Fact]
    public void Bail_FromRefused_Throws()
    {
        var signup = CreatePendingSignup();
        signup.Refuse(Guid.NewGuid(), Clock, null);

        var act = () => signup.Bail(Guid.NewGuid(), Clock, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkNoShow_FromConfirmed_SetsNoShow()
    {
        var signup = CreateConfirmedSignup();
        var reviewerId = Guid.NewGuid();
        var afterShift = new FakeClock(Now.Plus(Duration.FromHours(8)));

        signup.MarkNoShow(reviewerId, afterShift);

        signup.Status.Should().Be(SignupStatus.NoShow);
        signup.ReviewedByUserId.Should().Be(reviewerId);
    }

    [Fact]
    public void MarkNoShow_FromPending_Throws()
    {
        var signup = CreatePendingSignup();

        var act = () => signup.MarkNoShow(Guid.NewGuid(), Clock);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_FromConfirmed_SetsCancelled()
    {
        var signup = CreateConfirmedSignup();

        signup.Cancel(Clock, "Shift deactivated");

        signup.Status.Should().Be(SignupStatus.Cancelled);
        signup.StatusReason.Should().Be("Shift deactivated");
    }

    [Fact]
    public void Cancel_FromPending_SetsCancelled()
    {
        var signup = CreatePendingSignup();

        signup.Cancel(Clock, "Shift deactivated");

        signup.Status.Should().Be(SignupStatus.Cancelled);
    }

    [Fact]
    public void Cancel_FromBailed_Throws()
    {
        var signup = CreateConfirmedSignup();
        signup.Bail(Guid.NewGuid(), Clock, null);

        var act = () => signup.Cancel(Clock, null);

        act.Should().Throw<InvalidOperationException>();
    }
}
