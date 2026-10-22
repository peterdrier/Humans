using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Email.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Humans.Email.Tests;

/// <summary>
/// The /Settings#email tab. <c>Policy = AdminOnly</c> means
/// <c>SettingsTabComposition</c> drops this tab for a non-admin before it ever reaches
/// the view component — the negative case for who sees it.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansTheory]
    [Xunit.InlineData(null, false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("   ", false)]
    [Xunit.InlineData("\t", false)]
    [Xunit.InlineData("smtp.example.org", false)]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("", true)]
    [Xunit.InlineData("   ", true)]
    [Xunit.InlineData("\t", true)]
    [Xunit.InlineData("smtp.example.org", true)]
    public async Task Production_uses_smtp_and_reports_missing_configuration_at_send_time(string? host, bool production)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Email:SmtpHost"] = host }).Build();
        var registrations = new ServiceCollection().AddLogging();
        registrations.Configure<EmailSettings>(settings => settings.SmtpHost = host ?? string.Empty);
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(production ? Environments.Production : Environments.Development);
        registrations.AddSingleton(environment);
        new Section().Register(registrations, configuration);
        using var services = registrations.BuildServiceProvider();
        using var scope = services.CreateScope();

        var transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();
        var missing = string.IsNullOrWhiteSpace(host);
        transport.GetType().Should().Be(missing && !production ? typeof(StubEmailTransport) : typeof(SmtpEmailTransport));
        if (missing && production)
        {
            var send = () => transport.SendAsync("member@example.org", "Member", "Subject", "<p>Body</p>", null);
            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Email:SmtpHost*");
        }
    }

    [HumansFact]
    public void Tabs_ReturnsTheEmailTabAdminOnly()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("email");
        tab.Label.Should().Be("Settings_TabEmail");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
