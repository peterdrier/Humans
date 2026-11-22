using AwesomeAssertions;
using Humans.Base.Demo;
using Humans.Development.Data;
using Humans.Development.Domain;
using Humans.Development.Services.Demo;
using Humans.Integration.Tests.Infrastructure;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Development;

public class DemoWorldTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    // Small enough that reset's purges, about a second and a half each, fit CI's five-minute quiet limit.
    private const int CastSize = 30;

    private WebApplicationFactory<Program> _demoHost = null!;

    [HumansFact(Timeout = 300000)]
    public async Task Build_twice_adds_nothing_the_second_time_and_reset_retires_through_purge()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var eventId = await Factory.SeedActiveEventAsync();
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IShiftSeeding>().SetShiftBrowsingOpenAsync(eventId, isOpen: true);

        // Started after the seeding, so its caches load the open event.
        await using var demoHost = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<DemoCastOptions>(o => o.Size = CastSize)));
        _demoHost = demoHost;

        var first = await RunAsync(reset: false, adminId, ct);
        first.Should().NotContain(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
        var crowd = await MadeCrowdAsync(generation: 1, ct);
        crowd.Should().HaveCount(CastSize);
        (await UserInfosAsync(crowd, ct)).Should().AllSatisfy(u => u.IsTombstone.Should().BeFalse());
        (await LedgerAsync(ct)).Where(e => e.Key.StartsWith(RotaKeyPrefix, StringComparison.Ordinal))
            .Should().NotBeEmpty().And.AllSatisfy(e => e.State.Should().Be(DemoLedgerState.Made));

        var made = (await LedgerAsync(ct)).Count;
        await RunAsync(reset: false, adminId, ct);
        (await LedgerAsync(ct)).Should().HaveCount(made, "a second build adds nothing");

        var reset = await RunAsync(reset: true, adminId, ct);
        reset.Should().Contain("Generation 1 retired.");
        (await UserInfosAsync(crowd, ct)).Should().AllSatisfy(u => u.IsTombstone.Should().BeTrue());
        (await MadeCrowdAsync(generation: 2, ct)).Should().HaveCount(CastSize)
            .And.NotIntersectWith(crowd);
        (await LedgerAsync(ct)).Where(e => e.Generation == 1 && e.Key.StartsWith(TeamsDemo.TeamKeyPrefix, StringComparison.Ordinal))
            .Should().NotBeEmpty().And.AllSatisfy(e => e.State.Should().Be(DemoLedgerState.Retired));
        (await LedgerAsync(ct)).Where(e => e.Generation == 1 && e.Key.StartsWith(RotaKeyPrefix, StringComparison.Ordinal))
            .Should().AllSatisfy(e => e.State.Should().Be(DemoLedgerState.Retired));
    }

    private const string RotaKeyPrefix = "shifts:rota:";

    private async Task<IReadOnlyList<string>> RunAsync(bool reset, Guid actorId, CancellationToken ct)
    {
        using var scope = _demoHost.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoWorldRunner>().RunAsync(reset, actorId, ct);
    }

    private async Task<IReadOnlyList<DemoLedgerEntry>> LedgerAsync(CancellationToken ct) =>
        await _demoHost.Services.GetRequiredService<IDemoLedgerRepository>().GetAllAsync(ct);

    private async Task<List<Guid>> MadeCrowdAsync(int generation, CancellationToken ct) =>
        [.. (await LedgerAsync(ct))
            .Where(e => e.Generation == generation && e.State == DemoLedgerState.Made
                && e.Key.StartsWith(DemoCast.HumanKeyPrefix, StringComparison.Ordinal))
            .Select(e => e.EntityId)];

    private async Task<List<UserInfo>> UserInfosAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        using var scope = _demoHost.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var infos = new List<UserInfo>();
        foreach (var id in ids)
            infos.Add((await users.GetUserInfoAsync(id, ct))!);
        return infos;
    }
}
