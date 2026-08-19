using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Base.Interfaces;

/// <summary>
/// The health checks a section owns. Runs at builder time, so the section adds its own
/// checks with their own names and tags — the names are monitoring keys and must not change.
/// </summary>
public interface ISectionHealthChecks : ISectionContribution
{
    void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration);
}
