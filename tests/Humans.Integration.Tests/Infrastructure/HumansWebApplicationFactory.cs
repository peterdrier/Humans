using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;
using Humans.Application.Interfaces;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

public class HumansWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public IReadOnlyList<ServiceDescriptor> RegisteredServices { get; private set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Override connection string and provide required config keys.
            // Program.cs reads ConnectionStrings:DefaultConnection to build the
            // NpgsqlDataSource and DbContext, so overriding here is sufficient.
            config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["Authentication:Google:ClientId"] = "test-client-id",
                ["Authentication:Google:ClientSecret"] = "test-client-secret",
                ["Email:SmtpHost"] = "localhost",
                ["Email:FromAddress"] = "test@example.com",
                ["Email:BaseUrl"] = "https://localhost",
                ["GitHub:Owner"] = "",
                ["GitHub:Repository"] = "",
                ["GitHub:AccessToken"] = "",
                ["GoogleMaps:ApiKey"] = "test-api-key",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace email service with a no-op stub
            // so integration tests don't depend on Hangfire's job-storage
            // globals, which are intentionally disabled in Testing.
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailService));
            if (emailDescriptor != null)
                services.Remove(emailDescriptor);
            services.AddScoped(_ => Substitute.For<IEmailService>());

            RegisteredServices = services.ToList();

        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}
