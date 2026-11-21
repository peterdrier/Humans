using AwesomeAssertions;
using Humans.Development.Data;
using Humans.Development.Domain;
using Humans.Development.Services.Demo;
using Humans.Integration.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Development;

public class DemoWorldTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 600000)]
    public async Task Build_twice_adds_nothing_the_second_time_and_reset_retires_through_purge()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var first = await RunAsync(reset: false, adminId, ct);
        first.Should().NotContain(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
        var crowd = await MadeCrowdAsync(generation: 1, ct);
        crowd.Should().HaveCount(DemoCastStory.CrowdSize);
        (await UserInfosAsync(crowd, ct)).Should().AllSatisfy(u => u.IsTombstone.Should().BeFalse());

        await RunAsync(reset: false, adminId, ct);
        (await LedgerAsync(ct)).Should().HaveCount(DemoCastStory.CrowdSize, "a second build adds nothing");

        var reset = await RunAsync(reset: true, adminId, ct);
        reset.Should().Contain("Generation 1 retired.");
        (await UserInfosAsync(crowd, ct)).Should().AllSatisfy(u => u.IsTombstone.Should().BeTrue());
        (await MadeCrowdAsync(generation: 2, ct)).Should().HaveCount(DemoCastStory.CrowdSize)
            .And.NotIntersectWith(crowd);
    }

    private async Task<IReadOnlyList<string>> RunAsync(bool reset, Guid actorId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoWorldRunner>().RunAsync(reset, actorId, ct);
    }

    private async Task<IReadOnlyList<DemoLedgerEntry>> LedgerAsync(CancellationToken ct) =>
        await Factory.Services.GetRequiredService<IDemoLedgerRepository>().GetAllAsync(ct);

    private async Task<List<Guid>> MadeCrowdAsync(int generation, CancellationToken ct) =>
        [.. (await LedgerAsync(ct))
            .Where(e => e.Generation == generation && e.State == DemoLedgerState.Made)
            .Select(e => e.EntityId)];

    private async Task<List<UserInfo>> UserInfosAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var infos = new List<UserInfo>();
        foreach (var id in ids)
            infos.Add((await users.GetUserInfoAsync(id, ct))!);
        return infos;
    }
}
