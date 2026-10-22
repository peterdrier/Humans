using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.Extensions.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Sections;

public class SectionConfigurationSeamTests
{
    /// <summary>Declared by Shell because the host is what reads them.</summary>
    private static readonly string[] HostOwnedKeys =
    [
        "GitHub:Owner", "GitHub:Repository", "GitHub:AccessToken",
        "DevAuth:Enabled", "DevAuth:AllowAdmin"
    ];

    [HumansFact]
    public void Registry_Lists_Exactly_The_Host_Keys_Plus_Every_Seam_Contribution()
    {
        var configuration = new ConfigurationBuilder().Build();

        var fromSeams = new ConfigurationRegistry();
        foreach (var section in SectionDiscoveryExtensions.DiscoverImplementations<ISectionConfiguration>())
        {
            section.DeclareSettings(configuration, fromSeams);
        }

        var composed = new ConfigurationRegistry();
        new ServiceCollection().AddConfigurationMetadata(configuration, composed);

        composed.GetAll().Select(e => e.Key).Should().BeEquivalentTo(
            fromSeams.GetAll().Select(e => e.Key).Concat(HostOwnedKeys));
    }

    /// <summary>An empty contribution is a seam implemented and left unfilled.</summary>
    [HumansFact]
    public void Every_Contribution_Declares_At_Least_One_Key()
    {
        var configuration = new ConfigurationBuilder().Build();
        var contributions = SectionDiscoveryExtensions.DiscoverImplementations<ISectionConfiguration>();

        contributions.Should().NotBeEmpty();

        foreach (var contribution in contributions)
        {
            var registry = new ConfigurationRegistry();
            contribution.DeclareSettings(configuration, registry);
            registry.GetAll().Should().NotBeEmpty(
                "{0} implements the seam", contribution.GetType().FullName);
        }
    }
}
