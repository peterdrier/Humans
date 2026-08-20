using Humans.Base.Interfaces;
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
/// The type still exists because it is the whole marker: implementing <c>ISection</c> is what
/// makes the assembly a section for discovery, controller routing, the resource-set scan and
/// the analyzers. Drop it and the section's pages 404 with a green build.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally empty — see the remarks above.
    }
}
