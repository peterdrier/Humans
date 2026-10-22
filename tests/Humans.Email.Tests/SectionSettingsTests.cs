using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Email.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email.Tests;

/// <summary>
/// The /Settings#email tab (peterdrier/Humans#1634). <c>Policy = AdminOnly</c> means
/// <c>SettingsTabComposition</c> drops this tab for a non-admin before it ever reaches
/// the view component — the negative case for who sees it.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansTheory]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("", true)]
    [Xunit.InlineData("   ", true)]
    [Xunit.InlineData("\t", true)]
    [Xunit.InlineData("smtp.example.org", false)]
    public void Transport_uses_the_stub_only_when_no_smtp_host_is_configured(string? host, bool stub)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Email:SmtpHost"] = host }).Build();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddOptions();
        new Section().Register(registrations, configuration);
        using var services = registrations.BuildServiceProvider();
        using var scope = services.CreateScope();

        var transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();

        transport.GetType().Should().Be(stub ? typeof(StubEmailTransport) : typeof(SmtpEmailTransport));
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
