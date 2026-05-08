using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary>
/// Integration tests for <see cref="VolunteerTrackingRepository"/>. Mirrors the
/// repo's established service-test shape (e.g. <c>CalendarServiceTests</c>):
/// uses <see cref="IClassFixture{T}"/> for the test-container-backed factory,
/// resolves the Scoped <see cref="HumansDbContext"/> per test through a DI
/// scope, and exercises the repository against a real PostgreSQL container.
///
/// <see cref="IntegrationTestBase"/> is HttpClient-only, so it doesn't fit
/// repository tests; we use the factory directly per the
/// <c>CalendarServiceTests</c> pattern.
/// </summary>
public class VolunteerTrackingRepositoryTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public VolunteerTrackingRepositoryTests(HumansWebApplicationFactory factory) =>
        _factory = factory;

    [HumansFact]
    public async Task GetAsync_returns_null_when_no_row_exists()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var sut = new VolunteerTrackingRepository(db);

        var result = await sut.GetAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }
}
