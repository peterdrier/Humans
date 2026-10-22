using AwesomeAssertions;
using Hangfire;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
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
/// ValidateOnBuild can't see through <c>sp =&gt; sp.GetRequiredService&lt;T&gt;()</c> forwarding factories, so a
/// constructor cycle across two sections would recurse forever at the first request instead of failing at startup.
/// </summary>
public sealed class SectionServiceResolutionTests
{
    public static TheoryData<Type> CrossSectionServices => new()
    {
        typeof(IExpenseReportService),
        typeof(IHoldedFinanceService),
    };

    [HumansTheory]
    [MemberData(nameof(CrossSectionServices))]
    public async Task Service_resolves_in_a_scope(Type serviceType)
    {
        await using var factory = new NoDatabaseFactory();
        var provider = factory.Services;
        object? resolved = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var scope = provider.CreateScope();
                resolved = scope.ServiceProvider.GetRequiredService(serviceType);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };
        worker.Start();

        worker.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            because: $"{serviceType.Name} must resolve; a hang means a constructor cycle");
        failure.Should().BeNull();
        resolved.Should().NotBeNull();
    }

    private sealed class NoDatabaseFactory : WebApplicationFactory<Program>
    {
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
            });
        }
    }
}
