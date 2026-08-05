using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Base for every integration test class. <see cref="HumansWebApplicationFactory"/>
/// is an assembly fixture — one container, one app boot, one set of Singleton
/// stubs for the whole run — so returning those stubs to a pristine state before
/// each test is this type's job.
/// </summary>
public abstract class IntegrationTestBase
{
    protected readonly HttpClient Client;
    protected readonly HumansWebApplicationFactory Factory;

    protected IntegrationTestBase(HumansWebApplicationFactory factory)
    {
        // The factory (and its single-instance NSubstitute stubs) is shared by
        // every test in the assembly. This constructor runs once per test, so
        // reset the shared substitutes here to guarantee no mutation leaks
        // between tests — the P7 precondition that makes sharing safe.
        factory.ResetSharedSubstitutes();

        Client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            // Don't follow redirects so we can assert on redirect responses
            AllowAutoRedirect = false
        });
        Factory = factory;
    }
}
