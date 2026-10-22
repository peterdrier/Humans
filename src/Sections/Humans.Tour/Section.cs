using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tour;

/// <summary>
/// <c>Register</c> is empty, but the type must stay: implementing <c>ISection</c> is what makes this assembly a
/// section.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
