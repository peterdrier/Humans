using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Gate.Contracts;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

namespace Humans.Gate.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Gate
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/GateArchitectureTests.cs</c>, whose four
/// tests pinned the Application/Infrastructure split the section no longer has — that
/// <c>GateService</c> sat in <c>Humans.Application.Services.Gate</c>, took no DbContext, and
/// reached its tables through <c>IGateRepository</c>. One assembly with one internal surface
/// subsumes the first three; the cross-section read assertion is kept below because it is
/// about Tickets, not about where Gate lives.
/// </remarks>
public class GateArchitectureTests
{




    /// <summary>
    /// Pins the set of types that may inject <see cref="IGateRepository"/>: the owning service
    /// and the repository implementation. A new consumer taking the repository directly would
    /// bypass the service layer and the single-writer rule for the <c>gate_*</c> tables.
    /// </summary>
    [HumansFact]
    public void IGateRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Gate.Services.GateService",
            "Humans.Gate.Data.GateRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IGateRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the gate_* tables must go through the section's service");
    }

    [HumansFact]
    public void GateService_ReadsTicketsViaReadInterface()
    {
        var paramTypes = typeof(GateService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketServiceRead));

        // The full ticket service is internal to Humans.Tickets since that section's G5 move,
        // so it can no longer be named here — assert on the namespace instead, which also
        // catches a future Tickets type that is public but not on the contracts leaf.
        paramTypes
            .Where(t => t.Namespace?.StartsWith("Humans.Tickets", StringComparison.Ordinal) == true)
            .Should().OnlyContain(t => t.Namespace == "Humans.Tickets.Contracts",
                because: "cross-section ticket reads must use the contracts leaf (section-read-write-split / HUM0032)");
    }

    [HumansFact]
    public void GateEntities_HaveNoCrossSectionNavigationProperties()
    {
        // gate_scan_events links out to Users, Tickets and the authorizing supervisor purely by
        // bare Guid (design-rules §6c); resolve via IUserServiceRead / ITicketServiceRead.
        typeof(GateScanEvent).GetProperty("GuestUser").Should().BeNull();
        typeof(GateScanEvent).GetProperty("ScannedByUser").Should().BeNull();
        typeof(GateScanEvent).GetProperty("TicketAttendee").Should().BeNull();
        typeof(GateStaffPin).GetProperty("User").Should().BeNull();

        typeof(GateScanEvent).GetProperty("GuestUserId").Should().NotBeNull();
        typeof(GateScanEvent).GetProperty("TicketAttendeeId").Should().NotBeNull();
        typeof(GateStaffPin).GetProperty("UserId").Should().NotBeNull();
    }

    [HumansFact]
    public void ServiceImplementsTheUserLifecycleContracts()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(GateService))
            .Should().BeTrue(
                because: "Gate owns gate_scan_events and gate_staff_pins (user-scoped); it must "
                       + "contribute to the GDPR Article 15 export");
        typeof(IUserMerge).IsAssignableFrom(typeof(GateService))
            .Should().BeTrue(
                because: "GuestUserId / ScannedByUserId / OverrideByUserId are re-pointed on account merge");
    }

    [HumansFact]
    public void SectionRegistersTheContractsAndTheUserLifecycleForwarders()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(IGateRepository)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IGateService));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IGateScanRetention));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserMerge));
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Gate ships no resource set — every string on the kiosk is inline English by design
        // (the terminal is staff-facing and single-locale). A type that acquired an
        // IStringLocalizer<SharedResource> here would resolve against Humans.UI's set from
        // inside a section RCL, which is the failure §15 step 3b exists to prevent; a type
        // that needs localized copy needs a GateResource carve first.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Gate has no GateResource; a localizer here would resolve against another "
                   + "section's set and render raw keys (§15 step 3b)");
    }


}
