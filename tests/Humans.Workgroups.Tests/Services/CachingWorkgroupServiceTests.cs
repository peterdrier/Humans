using Xunit;
using AwesomeAssertions;
using Humans.Workgroups.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Workgroups.Tests.Services;

public sealed class CachingWorkgroupServiceTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IWorkgroupService _inner = Substitute.For<IWorkgroupService>();
    private readonly CachingWorkgroupService _service;

    public CachingWorkgroupServiceTests()
    {
        IReadOnlyList<WorkgroupInfo> register = [];
        _inner.GetRegisterAsync(Arg.Any<CancellationToken>()).Returns(register);

        var services = new ServiceCollection();
        services.AddKeyedScoped<IWorkgroupService>(
            CachingWorkgroupService.InnerServiceKey,
            (_, _) => _inner);
        var provider = services.BuildServiceProvider();

        _service = new CachingWorkgroupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingWorkgroupService>.Instance);
    }

    [HumansFact]
    public async Task GetRegister_SecondRead_IsServedFromCache()
    {
        var first = await _service.GetRegisterAsync(Ct);
        var second = await _service.GetRegisterAsync(Ct);

        second.Should().BeSameAs(first);
        await _inner.Received(1).GetRegisterAsync(Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GetRegister_LoadStartedBeforeWrite_DoesNotRepopulateCache(bool returnsId)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<WorkgroupInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inner.GetRegisterAsync(Ct).Returns(pending.Task, Task.FromResult<IReadOnlyList<WorkgroupInfo>>([]));
        var read = _service.GetRegisterAsync(Ct);

        if (returnsId)
            await _service.AddCommentAsync(Guid.NewGuid(), Guid.NewGuid(), "Clarification", "Comment", Ct);
        else
            await _service.JoinAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);

        pending.SetResult([]);
        await read;
        await _service.GetRegisterAsync(Ct);

        await _inner.Received(2).GetRegisterAsync(Ct);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_ForwardsTheResultAndClearsTheRegisterCache(bool refused)
    {
        var workgroupId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var expected = new WorkgroupMutationResult(refused ? new(WorkgroupErrorKeys.AlreadyAMember) : null);
        _inner.JoinAsync(workgroupId, userId, Ct).Returns(expected);
        await _service.GetRegisterAsync(Ct);

        (await _service.JoinAsync(workgroupId, userId, Ct)).Should().BeSameAs(expected);
        await _service.GetRegisterAsync(Ct);

        await _inner.Received(1).JoinAsync(workgroupId, userId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetRegisterAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Join_WhenInnerThrows_StillClearsTheRegisterCache()
    {
        _inner.JoinAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkgroupMutationResult>(new InvalidOperationException("write failed")));
        await _service.GetRegisterAsync(Ct);

        var act = () => _service.JoinAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _service.GetRegisterAsync(Ct);
        await _inner.Received(2).GetRegisterAsync(Arg.Any<CancellationToken>());
    }
}
