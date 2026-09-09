using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Scanner;

/// <summary>Scanner's DI entry point, discovered by Shell.</summary>
/// <remarks>
/// <c>Register</c> is empty: Scanner owns no tables and no services — <c>ScannerController</c>
/// injects other sections' read interfaces, each registered by its owner. The type stays
/// because implementing <c>ISection</c> is what makes the assembly a section for discovery,
/// controller routing and the resource-set scan; drop it and the pages 404 with a green build.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
