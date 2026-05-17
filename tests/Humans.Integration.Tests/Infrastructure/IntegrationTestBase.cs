using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

public abstract class IntegrationTestBase(HumansWebApplicationFactory factory)
    : IClassFixture<HumansWebApplicationFactory>
{
    protected readonly HttpClient Client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        // Don't follow redirects so we can assert on redirect responses
        AllowAutoRedirect = false
    });
    protected readonly HumansWebApplicationFactory Factory = factory;

    // Don't follow redirects so we can assert on redirect responses
}
