using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

public abstract class IntegrationTestBase : IClassFixture<HumansWebApplicationFactory>
{
    protected readonly HttpClient Client;
    protected readonly HumansWebApplicationFactory Factory;

    protected IntegrationTestBase(HumansWebApplicationFactory factory)
    {
        // The factory (and its single-instance NSubstitute stubs) is shared across
        // every test in the class via IClassFixture. This constructor runs once per
        // test, so reset the shared substitutes here to guarantee no mutation leaks
        // between tests — keeping the suite correct even under per-method parallelism.
        factory.ResetSharedSubstitutes();

        Client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            // Don't follow redirects so we can assert on redirect responses
            AllowAutoRedirect = false
        });
        Factory = factory;
    }
}
