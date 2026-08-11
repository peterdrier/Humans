using Humans.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Scanner;

/// <summary>
/// Scanner's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <c>Register</c> is empty, and that is the whole story: Scanner owns no tables and no
/// business logic. <c>ScannerController</c> is the section — it injects other sections'
/// read interfaces (Tickets, Users, EarlyEntry, Consent, Events, Shifts, ICalFeed), all of
/// which are registered by their own owners, and builds its view model inline. There was no
/// <c>AddScannerSection</c> in Shell to drain either.
/// <para>
/// The type still exists because <c>ISection</c> is what puts the assembly in
/// <c>SectionDiscoveryExtensions</c>'s discovered-sections log, which is the first thing you
/// read when one of a section's pages 404s (design §6). Its <c>[assembly: Section("Scanner")]</c>
/// marker — the analyzer, controller-discovery and resource-set seam — is in
/// <c>Properties/AssemblyInfo.cs</c> and is independent of this class.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally empty — see the remarks above.
    }
}
