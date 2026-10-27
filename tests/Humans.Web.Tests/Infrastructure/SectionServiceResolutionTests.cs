using AwesomeAssertions;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Resolves every Humans service the real <c>Program.cs</c> host registers, in a scope. Building the host runs
/// ValidateOnBuild and ValidateScopes (captive dependencies); resolving runs the
/// <c>sp =&gt; sp.GetRequiredService&lt;T&gt;()</c> forwarding factories those can't see into, so a constructor
/// cycle fails here instead of hanging at the first request. Nothing opens the database.
/// </summary>
public sealed class SectionServiceResolutionTests
{
    [HumansFact]
    public async Task Every_registered_service_resolves()
    {
        await using var factory = new NoDatabaseFactory();
        var provider = factory.Services;
        var current = "(none)";
        var failures = new List<string>();
        var worker = new Thread(() =>
        {
            using var scope = provider.CreateScope();
            foreach (var d in factory.Registrations.Where(IsHumansService))
            {
                current = Describe(d);
                try
                {
                    _ = d.IsKeyedService
                        ? scope.ServiceProvider.GetRequiredKeyedService(d.ServiceType, d.ServiceKey)
                        : scope.ServiceProvider.GetRequiredService(d.ServiceType);
                }
                catch (Exception ex)
                {
                    failures.Add($"{current}: {ex.GetBaseException().Message}");
                }
            }
            current = null!;
        })
        { IsBackground = true };
        worker.Start();

        worker.Join(TimeSpan.FromSeconds(10)).Should().BeTrue(
            because: $"resolution hung on {current}, which means a constructor cycle");
        failures.Should().BeEmpty();
    }

    private static bool IsHumansService(ServiceDescriptor d) =>
        !d.ServiceType.IsGenericTypeDefinition
        && (IsHumans(d.ServiceType)
            || IsHumans(d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType));

    private static bool IsHumans(Type? t) =>
        t?.Assembly.GetName().Name?.StartsWith("Humans.", StringComparison.Ordinal) == true;

    private static string Describe(ServiceDescriptor d) =>
        d.IsKeyedService ? $"{d.ServiceType.FullName} [{d.ServiceKey}]" : d.ServiceType.FullName!;

    private sealed class NoDatabaseFactory : WebApplicationFactory<Program>
    {
        public IReadOnlyList<ServiceDescriptor> Registrations { get; private set; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing skips Hangfire; the connection string is never opened.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection",
                "Host=localhost;Database=stub;Username=stub;Password=stub");
            builder.UseSetting("Email:SmtpHost", "localhost");
            builder.UseSetting("Email:FromAddress", "test@example.com");
            builder.UseSetting("Email:BaseUrl", "https://localhost");
            builder.UseSetting("GoogleMaps:ApiKey", "stub");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton(Substitute.For<IBackgroundJobClient>());
                Registrations = services.ToList();
            });
        }
    }
}
