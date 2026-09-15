using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

/// <summary>
/// Debug's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <see cref="Register"/> is empty: every singleton the pages read is registered by its owner,
/// and the section owns no tables. The class ships so Shell's section discovery finds the
/// assembly — its discovered-sections log is the first thing to read when a page 404s.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Nothing to register — see the remarks.
    }
}
