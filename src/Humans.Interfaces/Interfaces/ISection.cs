using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Base.Interfaces;

/// <summary>
/// A section's single DI entry point. Shell discovers implementations by walking the
/// entry assembly's dependency graph rather than naming them, so a section that moves
/// into its own project (nobodies-collective/Humans#866, G5) costs Shell no edit.
/// </summary>
/// <remarks>
/// The one <c>public</c> type a section exposes outside <c>Contracts/</c>. Implementations
/// are <c>public sealed class Section : ISection</c> at the section project's root — same
/// name in every section, since nothing names it. Activated with <see cref="Activator"/>,
/// so they must have a parameterless constructor and hold no state.
/// </remarks>
public interface ISection
{
    /// <summary>
    /// Registers the section's repositories, services, jobs, options and section-owned
    /// authorization handlers. Authorization <em>policies</em> stay in Shell (design §8).
    /// </summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
