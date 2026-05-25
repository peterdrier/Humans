using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Services.Mailer;

namespace Humans.Application.Tests.Architecture;

public class MailerArchitectureTests
{
    [HumansFact]
    public void IMailerLiteService_OnlyAllowsAudienceWrites()
    {
        var allowedWrites = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IMailerLiteService.CreateGroupAsync),
            nameof(IMailerLiteService.AssignSubscriberToGroupAsync),
            nameof(IMailerLiteService.UnassignSubscriberFromGroupAsync),
            nameof(IMailerLiteService.BulkImportSubscribersToGroupAsync),
        };

        var writePrefixes = new[]
        {
            "Create", "Update", "Delete", "Upsert", "Add", "Remove",
            "Set", "Post", "Put", "Patch", "Assign", "Unassign", "Bulk",
        };

        var unexpectedWrites = typeof(IMailerLiteService).GetMethods()
            .Where(m => writePrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal)))
            .Where(m => !allowedWrites.Contains(m.Name))
            .Select(m => m.Name)
            .ToList();

        unexpectedWrites.Should().BeEmpty(
            "IMailerLiteService writes are restricted to the four audience-management methods. " +
            "New writes need their own architecture review.");
    }

    [HumansFact]
    public void IMailerLiteService_LivesInMailerNamespace()
    {
        typeof(IMailerLiteService).Namespace
            .Should().Be("Humans.Application.Interfaces.Mailer");
    }

    [HumansFact]
    public void MailerImportService_DoesNotReferenceEFCore()
    {
        var asm = typeof(MailerImportService).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [HumansFact]
    public void IMailerLiteService_LivesInApplication_Interfaces()
    {
        typeof(IMailerLiteService).Assembly.GetName().Name
            .Should().Be("Humans.Application");
    }

    [HumansFact]
    public void AllAudiences_UseHumansPrefix()
    {
        var audienceType = typeof(IMailerAudience);
        var impls = typeof(MailerImportService).Assembly
            .GetTypes()
            .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToList();

        impls.Should().NotBeEmpty("at least one IMailerAudience implementation is expected.");

        foreach (var impl in impls)
        {
            var instance = (IMailerAudience)Activator.CreateInstance(impl, NonPublicConstructorBypass(impl))!;
            instance.MailerLiteGroupName.Should().StartWith("Humans - ",
                $"every IMailerAudience must target a Humans-prefixed group; {impl.Name} does not.");
        }
    }

    [HumansFact]
    public void AllAudiences_HaveUniqueGroupNamesAndKeys()
    {
        var audienceType = typeof(IMailerAudience);
        var impls = typeof(MailerImportService).Assembly
            .GetTypes()
            .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => (IMailerAudience)Activator.CreateInstance(t, NonPublicConstructorBypass(t))!)
            .ToList();

        impls.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count().Should().Be(impls.Count,
            "audience keys collide");
        impls.Select(a => a.MailerLiteGroupName).Distinct(StringComparer.Ordinal).Count().Should().Be(impls.Count,
            "audience group names collide");
    }

    [HumansFact]
    public void MailerAudienceSyncService_LivesInApplication_NoEF()
    {
        var serviceType = typeof(MailerAudienceSyncService);
        serviceType.Namespace.Should().Be("Humans.Application.Services.Mailer");
        serviceType.Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    // Reflection helper — passes null/default args to allow constructing audiences
    // that take service dependencies. The arch test only inspects metadata properties.
    private static object?[] NonPublicConstructorBypass(Type t)
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.GetParameters().Select(p =>
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
    }
}
