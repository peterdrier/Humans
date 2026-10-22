using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;

namespace Humans.Email.Tests;

/// <summary>
/// Email uses the no-decorator variant: outbox reads are sequential queue drains, not a hot
/// request path an in-memory dict would help, and admin dashboard reads are infrequent and small.
/// </summary>
public class EmailArchitectureTests
{
    [HumansFact]
    public void OutboxEmailService_TakesOutboxRepositoryAndNoAddressLookup()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEmailOutboxRepository),
            because: "outbox writes go through the Email section's repository");
        paramTypes.Should().NotContain(typeof(IUserEmailService),
            because: "every sender passes the recipient's UserId; Email never resolves it from the address (nobodies-collective/Humans#1245)");
    }

    [HumansFact]
    public void OutboxEmailService_TakesConnectorAbstractions()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEmailBodyComposer),
            because: "branded email wrapping captures IHostEnvironment + EmailSettings; the service takes the abstraction so it stays config-free");
        paramTypes.Should().Contain(typeof(IImmediateOutboxProcessor),
            because: "triggering an immediate outbox run uses Hangfire's IBackgroundJobClient — the service takes the abstraction rather than the Hangfire type");
    }

    /// <summary>
    /// The section carries Hangfire.Core (HangfireImmediateOutboxProcessor and the two jobs
    /// name it), so a Hangfire parameter on the service compiles. Scheduling stays behind
    /// IImmediateOutboxProcessor — containment of a dependency the section really has.
    /// </summary>
    [HumansFact]
    public void OutboxEmailService_HasNoHangfireDependency()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var hangfireParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Hangfire", StringComparison.Ordinal));

        hangfireParam.Should().BeNull(
            because: "IImmediateOutboxProcessor abstracts the dispatch — a direct Hangfire "
                   + "parameter bypasses the connector boundary");
    }

    [HumansFact]
    public void ConnectorAbstractions_SitOnTheSideTheirImplementationLivesOn()
    {
        // IImmediateOutboxProcessor stays on the contracts leaf because the Shell registers it.
        // IEmailBodyComposer is internal because both sides are.
        typeof(IImmediateOutboxProcessor).Namespace
            .Should().Be("Humans.Email.Contracts");
        typeof(IImmediateOutboxProcessor).IsPublic.Should().BeTrue();

        typeof(IEmailBodyComposer).Namespace
            .Should().Be("Humans.Email.Services");
        typeof(IEmailBodyComposer).IsPublic.Should().BeFalse();
    }
}
